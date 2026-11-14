package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"syscall"

	"heron/automation-tenants/internal/automation"
)

var version = "dev"
var commit = "unknown"

func main() {
	config := flag.String("config", "", "private automation tenant configuration")
	showVersion := flag.Bool("version", false, "print build version and source commit")
	flag.Parse()
	if *showVersion {
		fmt.Printf("Heron.AutomationTenants.Server %s %s\n", version, commit)
		return
	}
	if *config == "" {
		log.Fatal("Expected --config <private.json>")
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	if err := automation.Run(ctx, *config); err != nil {
		log.Printf("AutomationTenants stopped: %s", err)
		os.Exit(1)
	}
}
