package automation

import (
	"crypto/rand"
	"encoding/hex"
	"slices"
	"strings"
	"time"
	"unicode/utf8"
)

const controlCapability = "pregel.control.v1"

func queueControl(state *tenantState, run RemoteRun, actor string, request ReviewRequest) (RemoteRun, error) {
	machine := state.Machines[strings.ToLower(run.MachineName)]
	if !slices.Contains(machine.Capabilities, controlCapability) {
		return run, fault(409, "这台构建机需要更新 Agent 后才能在网页处理本次运行。")
	}
	if run.LocalRunID == "" {
		return run, fault(409, "机器尚未上报本次运行记录，请稍后再试。")
	}
	if previous := run.Control; previous != nil && previous.State == "pending" {
		if previous.Action == request.Action && previous.ReviewID == request.ReviewID && previous.ContentHash == request.ContentHash && previous.Feedback == request.Feedback {
			return run, nil
		}
		return run, fault(409, "已有处理请求等待机器确认，请先等待处理结果。")
	}
	key := make([]byte, 16)
	if _, err := rand.Read(key); err != nil {
		return run, err
	}
	now := time.Now().UTC()
	run.Control = &RunControl{ID: hex.EncodeToString(key), Action: request.Action, ReviewID: request.ReviewID,
		ContentHash: request.ContentHash, Feedback: request.Feedback, State: "pending", Detail: "已提交，等待机器处理",
		CreatedBy: actor, CreatedAt: now, UpdatedAt: now}
	run.Revision++
	run.UpdatedAt = now
	state.Runs[run.ID] = run
	return run, nil
}

const discardCapability = "pregel.discard.v1"

// A server-side decision is not proof that the execution stopped. Native runs
// remain nonterminal until their owner reports the result of the control.
func (s *service) discard(actor, selected, id string) (RemoteRun, error) {
	tenant, err := s.config.tenant(actor, selected)
	if err != nil {
		return RemoteRun{}, err
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	var result RemoteRun
	err = s.store.change(tenant.ID, func(state *tenantState) error {
		run, err := requireRun(state, id)
		if err != nil {
			return err
		}
		if terminal(run.State) {
			result = run
			return nil
		}
		if run.State != "awaiting_review" && !run.CancelRequested {
			return fault(409, "请先停止本次运行；停止无法完成时才能强制结束。")
		}
		machine := state.Machines[strings.ToLower(run.MachineName)]
		if run.LocalRunID != "" || run.AcceptedAt != nil || run.Source == "local" {
			request := ReviewRequest{Action: "stop"}
			if run.State == "awaiting_review" {
				if !slices.Contains(machine.Capabilities, discardCapability) {
					return fault(409, "执行机不支持结束本机待审核记录，未修改运行状态；请在本机处理。")
				}
				request.Action = "discard"
			}
			if _, err := queueControl(state, run, actor, request); err != nil {
				return err
			}
			cancelTree(state, id, true)
			run = state.Runs[id]
			run.Detail = "已请求执行机停止，等待本机终态确认"
			run.UpdatedAt = time.Now().UTC()
			state.Runs[id] = run
			result = run
			return nil
		}
		// No owner has accepted this request, so no native execution needs stopping.
		cancelTree(state, id, false)
		now := time.Now().UTC()
		run.State = "cancelled"
		run.CancelRequested = true
		run.LogsComplete = true
		run.Detail = "已由 " + actor + " 强制结束，未执行收尾"
		run.UpdatedAt = now
		run.Revision++
		state.Runs[id] = run
		result = run
		return nil
	})
	return result, err
}

func (s *service) review(actor, selected, id string, request ReviewRequest) (RemoteRun, error) {
	tenant, err := s.config.tenant(actor, selected)
	if err != nil {
		return RemoteRun{}, err
	}
	return s.reviewRun(tenant, actor, id, request)
}

func (s *service) reviewRun(tenant tenantConfig, actor, id string, request ReviewRequest) (RemoteRun, error) {
	if request.Action != "approve" && request.Action != "revise" && request.Action != "abandon" {
		return RemoteRun{}, fault(400, "审核动作无效。")
	}
	if request.ReviewID == "" || !clean(request.ReviewID, 512) || request.ContentHash == "" || !clean(request.ContentHash, 512) || utf8.RuneCountInString(request.Feedback) > 12000 {
		return RemoteRun{}, fault(400, "需要当前审核内容的身份、哈希及不超过 12000 字符的意见。")
	}
	if request.Action == "revise" && strings.TrimSpace(request.Feedback) == "" {
		return RemoteRun{}, fault(400, "请填写继续处理的要求或问题。")
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	var result RemoteRun
	err := s.store.change(tenant.ID, func(state *tenantState) error {
		run, err := requireRun(state, id)
		if err != nil {
			return err
		}
		header, present, err := snapshotInfo(run.Snapshot)
		if err != nil || !present || run.State != "awaiting_review" || header.Review == nil {
			return fault(409, "该运行已不在等待审核，请刷新后重试。")
		}
		if header.Review.ID != request.ReviewID || header.Review.ContentHash != request.ContentHash {
			return fault(409, "审核内容已经变化，请阅读新版本后重新处理。")
		}
		if request.Action == "revise" && header.Review.CanRevise != nil && !*header.Review.CanRevise {
			return fault(409, "此流程不支持修改意见。")
		}
		if run.CancelRequested && request.Action != "abandon" {
			return fault(409, "本次运行正在终止，不能继续审核。")
		}
		result, err = queueControl(state, run, actor, request)
		if err == nil && request.Action == "abandon" {
			cancelTree(state, id, true)
			result = state.Runs[id]
		}
		return err
	})
	return result, err
}

func (s *service) reportControl(id, controlID string, request ControlReport) (map[string]bool, error) {
	if request.State != "applied" && request.State != "failed" || len(request.Detail) > 8000 {
		return nil, fault(400, "处理回执格式无效。")
	}
	s.store.mu.Lock()
	defer s.store.mu.Unlock()
	assigned, err := s.agentRun(request.MachineName, id)
	if err != nil {
		return nil, err
	}
	accepted := false
	err = s.store.change(assigned.TenantID, func(state *tenantState) error {
		run := state.Runs[id]
		if run.Control == nil || run.Control.ID != controlID {
			return nil
		}
		accepted = true
		if run.Control.State != "pending" {
			return nil
		}
		now := time.Now().UTC()
		run.Control.State, run.Control.Detail, run.Control.UpdatedAt = request.State, request.Detail, now
		if request.State == "failed" && (run.Control.Action == "abandon" || run.Control.Action == "stop") {
			// Keep the native review actionable when its abandon request could not be applied.
			run.CancelRequested = false
		}
		run.Revision++
		run.UpdatedAt = now
		state.Runs[id] = run
		return nil
	})
	return map[string]bool{"accepted": accepted}, err
}
