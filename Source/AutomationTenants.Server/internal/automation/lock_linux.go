//go:build linux

package automation

import (
	"io"
	"os"
	"syscall"
)

type stateLock struct{ *os.File }

func lockState(path string) (io.Closer, error) {
	f, err := os.OpenFile(path, os.O_CREATE|os.O_RDWR, 0600)
	if err != nil {
		return nil, err
	}
	if err := syscall.Flock(int(f.Fd()), syscall.LOCK_EX|syscall.LOCK_NB); err != nil {
		f.Close()
		return nil, err
	}
	return &stateLock{f}, nil
}

func (l *stateLock) Close() error {
	_ = syscall.Flock(int(l.Fd()), syscall.LOCK_UN)
	return l.File.Close()
}
