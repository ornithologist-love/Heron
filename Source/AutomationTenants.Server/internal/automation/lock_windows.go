//go:build windows

package automation

import (
	"io"
	"os"
	"syscall"
	"unsafe"
)

type stateLock struct {
	file       *os.File
	overlapped syscall.Overlapped
}

var kernel32 = syscall.NewLazyDLL("kernel32.dll")
var lockFileEx = kernel32.NewProc("LockFileEx")
var unlockFileEx = kernel32.NewProc("UnlockFileEx")

func lockState(path string) (io.Closer, error) {
	f, err := os.OpenFile(path, os.O_CREATE|os.O_RDWR, 0600)
	if err != nil {
		return nil, err
	}
	lock := &stateLock{file: f}
	ok, _, err := lockFileEx.Call(f.Fd(), 3, 0, 0xffffffff, 0xffffffff, uintptr(unsafe.Pointer(&lock.overlapped)))
	if ok == 0 {
		f.Close()
		return nil, err
	}
	return lock, nil
}

func (l *stateLock) Close() error {
	_, _, _ = unlockFileEx.Call(l.file.Fd(), 0, 0xffffffff, 0xffffffff, uintptr(unsafe.Pointer(&l.overlapped)))
	return l.file.Close()
}
