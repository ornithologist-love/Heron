package automation

import (
	"errors"
	"regexp"
	"strings"
)

// The wire identities belong to the host: the framework has no product names of
// its own, so every installation states them and a missing value stops startup
// instead of silently changing an existing receiver's headers or realm.
type protocolConfig struct {
	WebhookHeaderPrefix    string `json:"webhook_header_prefix,omitempty"`
	MCPRealm               string `json:"mcp_realm,omitempty"`
	LegacyPublicationOwner string `json:"legacy_publication_owner,omitempty"`
	ActorHeader            string `json:"actor_header,omitempty"`
	AccessHeader           string `json:"access_header,omitempty"`
	MCPServerName          string `json:"mcp_server_name,omitempty"`
}

var protocolHeaderName = regexp.MustCompile(`^[A-Za-z][A-Za-z0-9-]{0,63}$`)

func (p *protocolConfig) configure() error {
	for _, value := range []struct {
		field string
		value string
	}{
		{"webhook_header_prefix", p.WebhookHeaderPrefix},
		{"mcp_realm", p.MCPRealm},
		{"legacy_publication_owner", p.LegacyPublicationOwner},
		{"actor_header", p.ActorHeader},
		{"access_header", p.AccessHeader},
		{"mcp_server_name", p.MCPServerName},
	} {
		if value.value == "" {
			return errors.New("protocol." + value.field + " is required")
		}
	}
	for _, header := range []string{p.WebhookHeaderPrefix, p.ActorHeader, p.AccessHeader} {
		if !protocolHeaderName.MatchString(header) {
			return errors.New("protocol header names must contain only ASCII letters, digits and hyphens")
		}
	}
	if !safeID.MatchString(p.LegacyPublicationOwner) || !clean(p.MCPRealm, 128) ||
		strings.ContainsAny(p.MCPRealm, `"\`) || !clean(p.MCPServerName, 128) {
		return errors.New("protocol realm, publication owner or server name is invalid")
	}
	return nil
}
