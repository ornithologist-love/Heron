package automation

import (
	"archive/zip"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"io"
	"net/http"
	"os"
	"path"
	"path/filepath"
	"strings"
	"time"
)

const maxPoolArchiveSize = 1 << 30
const maxPoolPackageSize = 64 << 20

func (s *service) poolPackage(id, machine, question, filename string) (resourceResponse, error) {
	validQuestion := (len(question) == 2 || len(question) == 3 && question[2] >= 'A' && question[2] <= 'Z') &&
		question[0] >= '0' && question[0] <= '9' && question[1] >= '0' && question[1] <= '9'
	hash, hasSuffix := strings.CutSuffix(filename, ".zip")
	digest, err := poolArchiveHash(hash)
	if !validQuestion || !hasSuffix || err != nil || digest != hash {
		return resourceResponse{}, fault(400, "题包路径无效。")
	}
	s.store.mu.Lock()
	assigned, _, err := s.poolArchiveRun(id, ChildAccessRequest{MachineName: machine}, true)
	directory := s.config.tenantByID[assigned.TenantID].PoolPackageDirectory
	s.store.mu.Unlock()
	if err != nil {
		return resourceResponse{}, err
	}
	if directory == "" {
		return resourceResponse{}, fault(404, "租户尚未配置评测题包目录。")
	}
	file, err := os.Open(filepath.Join(directory, "packages", question, filename))
	if errors.Is(err, os.ErrNotExist) {
		return resourceResponse{}, fault(404, "题包不存在。")
	}
	if err != nil {
		return resourceResponse{}, err
	}
	defer file.Close()
	content, err := io.ReadAll(io.LimitReader(file, maxPoolPackageSize+1))
	if err != nil {
		return resourceResponse{}, err
	}
	if len(content) > maxPoolPackageSize {
		return resourceResponse{}, fault(413, "题包超过 64 MiB。")
	}
	return resourceResponse{Status: 200, ContentType: "application/zip", Body: content}, nil
}

type poolArchiveReceipt struct {
	SHA256 string `json:"sha256"`
	Size   int64  `json:"size"`
}

func poolArchiveHash(value string) (string, error) {
	digest, err := hex.DecodeString(value)
	if err != nil || len(digest) != sha256.Size {
		return "", fault(400, "归档需要有效的 SHA-256。")
	}
	return hex.EncodeToString(digest), nil
}

func (s *store) poolArchivePath(run RemoteRun, digest string) string {
	return filepath.Join(s.directory, "tenants", run.TenantID, "runs", run.ID, "archives", digest+".zip")
}

func (s *store) checkPoolArchive(run RemoteRun) error {
	info, err := os.Lstat(s.poolArchivePath(run, run.ReceivedArchive.SHA256))
	if errors.Is(err, os.ErrNotExist) {
		return fault(409, "服务器归档文件缺失。")
	}
	if err != nil {
		return err
	}
	if !info.Mode().IsRegular() || info.Size() != run.ReceivedArchive.Size {
		return fault(409, "服务器归档文件大小与回执不符。")
	}
	return nil
}

func (s *service) receivePoolArchive(id string, r *http.Request, worker bool) (poolArchiveReceipt, error) {
	owner := ChildAccessRequest{MachineName: r.Header.Get("X-Pregel-Parent-Machine"),
		ParentLocalRunID: r.Header.Get("X-Pregel-Parent-Run")}
	if worker {
		owner.MachineName = r.URL.Query().Get("machine_name")
	}
	hashValue := r.Header.Get("X-Pregel-Archive-Sha256")
	if worker {
		hashValue = r.URL.Query().Get("sha256")
	}
	digest, err := poolArchiveHash(hashValue)
	if err != nil {
		return poolArchiveReceipt{}, err
	}
	if r.ContentLength <= 0 || r.ContentLength > maxPoolArchiveSize {
		return poolArchiveReceipt{}, fault(400, "归档需要有效 Content-Length，大小上限为 1 GiB。")
	}
	receipt := poolArchiveReceipt{SHA256: digest, Size: r.ContentLength}
	s.store.mu.Lock()
	_, run, err := s.poolArchiveRun(id, owner, worker)
	if err == nil {
		err = s.checkArchiveAdmission(run, receipt, worker)
	}
	s.store.mu.Unlock()
	if err != nil {
		return poolArchiveReceipt{}, err
	}
	if run.ReceivedArchive != nil && s.store.checkPoolArchive(run) == nil {
		return receipt, nil
	}

	destination := s.store.poolArchivePath(run, digest)
	directory := filepath.Dir(destination)
	if err := os.MkdirAll(directory, 0700); err != nil {
		return poolArchiveReceipt{}, err
	}
	staged, err := os.CreateTemp(directory, ".incoming-*")
	if err != nil {
		return poolArchiveReceipt{}, err
	}
	defer os.Remove(staged.Name())
	hash := sha256.New()
	size, readErr := io.Copy(io.MultiWriter(staged, hash), io.LimitReader(r.Body, receipt.Size+1))
	if readErr == nil && size == receipt.Size {
		readErr = staged.Sync()
	}
	closeErr := staged.Close()
	if readErr != nil {
		return poolArchiveReceipt{}, readErr
	}
	if closeErr != nil {
		return poolArchiveReceipt{}, closeErr
	}
	if size != receipt.Size || hex.EncodeToString(hash.Sum(nil)) != receipt.SHA256 {
		return poolArchiveReceipt{}, fault(409, "归档字节数或 SHA-256 校验失败。")
	}
	if err := validatePoolArchive(staged.Name()); err != nil {
		return poolArchiveReceipt{}, err
	}

	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	assigned, run, err := s.poolArchiveRun(id, owner, worker)
	if err != nil {
		return poolArchiveReceipt{}, err
	}
	if err := s.checkArchiveAdmission(run, receipt, worker); err != nil {
		return poolArchiveReceipt{}, err
	}
	if run.ReceivedArchive != nil && s.store.checkPoolArchive(run) == nil {
		return receipt, nil
	}
	if err := os.Rename(staged.Name(), destination); err != nil {
		return poolArchiveReceipt{}, err
	}
	err = s.store.change(assigned.TenantID, func(state *tenantState) error {
		run.ReceivedArchive = &receipt
		run.Revision++
		run.UpdatedAt = time.Now().UTC()
		state.Runs[id] = run
		return nil
	})
	return receipt, err
}

// The caller holds store.mu. Workers use the same ownership check as status and log receipts.
func (s *service) poolArchiveRun(id string, owner ChildAccessRequest, worker bool) (assignment, RemoteRun, error) {
	if !worker {
		return s.childForParent(id, owner)
	}
	assigned, err := s.agentRun(owner.MachineName, id)
	if err != nil {
		return assigned, RemoteRun{}, err
	}
	run := s.store.states[assigned.TenantID].Runs[id]
	if !run.PullFromPool || run.AcceptedAt == nil {
		return assigned, RemoteRun{}, fault(409, "只有已领单的机器池运行可以接收评测归档。")
	}
	return assigned, run, nil
}

// The caller holds store.mu, both before transferring bytes and at commit.
func (s *service) checkArchiveAdmission(run RemoteRun, receipt poolArchiveReceipt, worker bool) error {
	if run.ReceivedArchive != nil {
		if *run.ReceivedArchive != receipt {
			return fault(409, "运行已经保存另一份归档。")
		}
		return nil
	}
	if !poolChildHoldsMachine(run) || (!worker && (!terminal(run.State) || run.State == "unknown" || !run.LogsComplete)) {
		return fault(409, "归档前需确认该运行已结束并完成日志回传。")
	}
	return nil
}

func validatePoolArchive(filename string) error {
	archive, err := zip.OpenReader(filename)
	if err != nil {
		return fault(400, "归档必须是有效 ZIP。")
	}
	defer archive.Close()
	paths := make(map[string]bool, len(archive.File))
	hasFile := false
	for _, entry := range archive.File {
		name := strings.TrimSuffix(entry.Name, "/")
		pathKey := strings.ToLower(name)
		if name == "" || name == "." || name == ".." || path.IsAbs(name) ||
			path.Clean(name) != name || strings.HasPrefix(name, "../") ||
			strings.ContainsAny(name, "\\:") || paths[pathKey] ||
			(!entry.Mode().IsRegular() && !entry.Mode().IsDir()) {
			return fault(400, "归档含有无效路径、重复项或特殊文件。")
		}
		paths[pathKey] = true
		hasFile = hasFile || entry.Mode().IsRegular()
	}
	if !hasFile {
		return fault(400, "归档不能是空 ZIP。")
	}
	return nil
}
