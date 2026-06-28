# Supported Session Settings

QuickFIX/n session configuration is a `.cfg` file with one `[DEFAULT]` section and one or more `[SESSION]` sections. Settings in `[DEFAULT]` apply to every session; a matching setting in `[SESSION]` overrides the default for that session only.

---

## Session Identity & Routing

| Setting | Section | Type | Required | Description |
|---|---|---|---|---|
| `BeginString` | SESSION | string | Yes | FIX protocol version. Valid: `FIX.4.0`, `FIX.4.1`, `FIX.4.2`, `FIX.4.3`, `FIX.4.4`, `FIXT.1.1`, `FIX.5.0`, `FIX.5.0SP1`, `FIX.5.0SP2` |
| `SenderCompID` | SESSION | string | Yes | Local participant ID written to FIX tag 49 |
| `SenderSubID` | SESSION | string | No | Local sub-ID qualifier (tag 50) |
| `SenderLocationID` | SESSION | string | No | Local location ID qualifier (tag 142) |
| `TargetCompID` | SESSION | string | Yes | Counterparty participant ID (tag 56) |
| `TargetSubID` | SESSION | string | No | Counterparty sub-ID qualifier (tag 57) |
| `TargetLocationID` | SESSION | string | No | Counterparty location ID qualifier (tag 143) |
| `SessionQualifier` | SESSION | string | No | Initiator only. Extra disambiguator when SenderCompID/TargetCompID pair is reused across sessions |

---

## Connection Type & Transport

| Setting | Section | Type | Required | Description |
|---|---|---|---|---|
| `ConnectionType` | DEFAULT/SESSION | string | Yes | Session role. Valid: `initiator`, `acceptor` |
| `SocketConnectHost` | SESSION | string | Initiator | Hostname or IP to connect to |
| `SocketConnectPort` | SESSION | int | Initiator | TCP port to connect to |
| `SocketConnectHost<N>` | SESSION | string | No | Failover host, e.g. `SocketConnectHost1`, `SocketConnectHost2`. Tried in order after primary |
| `SocketConnectPort<N>` | SESSION | int | No | Failover port paired with the same `<N>` suffix |
| `SocketAcceptHost` | SESSION | string | No | Acceptor only. Bind address (default: all interfaces) |
| `SocketAcceptPort` | SESSION | int | Acceptor | Acceptor only. Listen TCP port |
| `ReconnectInterval` | DEFAULT/SESSION | int | No | Initiator reconnect delay in seconds (default: `30`) |

---

## Heartbeat & Timing

| Setting | Section | Type | Required | Description |
|---|---|---|---|---|
| `HeartBtInt` | SESSION | int | Initiator | Heartbeat interval in seconds proposed on Logon (tag 108) |
| `StartTime` | SESSION | string | No | Session start time in `HH:MM:SS` (UTC unless `UseLocalTime` or `TimeZone` set) |
| `EndTime` | SESSION | string | No | Session end time in `HH:MM:SS` |
| `StartDay` | SESSION | string | No | Weekly session start day. Requires `EndDay`. Valid: `Su`, `Mo`, `Tu`, `We`, `Th`, `Fr`, `Sa` (or full names) |
| `EndDay` | SESSION | string | No | Weekly session end day. Paired with `StartDay` |
| `Weekdays` | SESSION | string | No | Comma-separated active weekdays, e.g. `Mo,Tu,We,Th,Fr`. Mutually exclusive with `StartDay`/`EndDay` |
| `NonStopSession` | SESSION | bool | No | `Y` for 24/7 session with no daily reset. Mutually exclusive with time-based scheduling (default: `N`) |
| `UseLocalTime` | SESSION | bool | No | Interpret `StartTime`/`EndTime` as local wall-clock rather than UTC. Mutually exclusive with `TimeZone` (default: `N`) |
| `TimeZone` | SESSION | string | No | Windows `TimeZoneInfo` ID for session times, e.g. `Eastern Standard Time`, `UTC`. Mutually exclusive with `UseLocalTime` |

---

## Message Persistence & State

| Setting | Section | Type | Required | Description |
|---|---|---|---|---|
| `FileStorePath` | DEFAULT/SESSION | string | No | Directory for sequence number and message stores. No store if omitted (memory-only) |
| `FileLogPath` | DEFAULT/SESSION | string | No | Directory for event and message audit logs. No file log if omitted |
| `PersistMessages` | SESSION | bool | No | Persist outbound messages for replay on resend requests. `N` sends gap-fills instead (default: `Y`) |
| `RefreshOnLogon` | SESSION | bool | No | Restore sequence state from the store on logon without resetting. Enables hot-failover (default: `N`) |
| `ResetOnLogon` | SESSION | bool | No | Reset sequence numbers to 1 on every logon (default: `N`) |
| `ResetOnLogout` | SESSION | bool | No | Reset sequence numbers to 1 after a clean logout (default: `N`) |
| `ResetOnDisconnect` | SESSION | bool | No | Reset sequence numbers to 1 after an abnormal disconnect (default: `N`) |

---

## Timeouts & Teardown

| Setting | Section | Type | Required | Description |
|---|---|---|---|---|
| `LogonTimeout` | SESSION | int | No | Seconds to wait for a Logon response before disconnecting (default: `10`) |
| `LogoutTimeout` | SESSION | int | No | Seconds to wait for a Logout response before disconnecting (default: `2`) |
| `SendLogoutBeforeDisconnectFromTimeout` | SESSION | bool | No | Send a Logout message before disconnecting on timeout (default: `N`) |

---

## Data Dictionary & Validation

| Setting | Section | Type | Required | Description |
|---|---|---|---|---|
| `UseDataDictionary` | DEFAULT/SESSION | bool | No | Enable data dictionary validation (default: `Y`) |
| `DataDictionary` | SESSION | string | No | Path to FIX 4.x data dictionary XML. Default derived from `BeginString` (e.g. `FIX42.xml`) |
| `TransportDataDictionary` | SESSION | string | No | FIXT sessions only. Path to FIXT.1.1 transport dictionary |
| `AppDataDictionary` | SESSION | string | No | FIXT sessions only. Path to application-layer dictionary |
| `AppDataDictionary.<version>` | SESSION | string | No | FIXT sessions only. Per-version app dictionary, e.g. `AppDataDictionary.FIX.5.0` |
| `DefaultApplVerID` | SESSION | string | FIXT | Required for FIXT sessions. FIX application version. Valid: `FIX.5.0`, `FIX.5.0SP1`, `FIX.5.0SP2`, or ApplVerID numeric values (`2`–`9`) |
| `ValidateFieldsOutOfOrder` | SESSION | bool | No | Reject messages with fields out of spec-defined order (default: `N`) |
| `ValidateFieldsHaveValues` | SESSION | bool | No | Reject messages with empty field values (default: `N`) |
| `ValidateUserDefinedFields` | SESSION | bool | No | Validate user-defined fields (tag > 9999) against the dictionary (default: `N`) |
| `ValidateLengthAndChecksum` | SESSION | bool | No | Validate `BodyLength` (tag 9) and `CheckSum` (tag 10) (default: `Y`) |
| `AllowUnknownEnumValues` | SESSION | bool | No | Accept messages with unrecognised enum field values (default: `N`) |
| `AllowUnknownMsgFields` | SESSION | bool | No | Accept messages containing fields not in the dictionary (default: `N`) |
| `AllowStringTruncationForCharFields` | SESSION | bool | No | **fpsim enhancement.** Silently truncate multi-character strings to single-char fields rather than rejecting (default: `N`) |

---

## Resend & Gap Fill

| Setting | Section | Type | Required | Description |
|---|---|---|---|---|
| `SendRedundantResendRequests` | SESSION | bool | No | Re-issue resend requests when a partial fill arrives (assists laggy counterparties) (default: `N`) |
| `ResendSessionLevelRejects` | SESSION | bool | No | Include session-level rejects (MsgType `3`) in resend responses (default: `N`) |
| `IgnorePossDupResendRequests` | SESSION | bool | No | Ignore resend requests that carry `PossDupFlag=Y` (default: `N`) |
| `MaxMessagesInResendRequest` | SESSION | ulong | No | Cap on messages per individual resend request. `0` = unlimited (default: `0`) |
| `RequiresOrigSendingTime` | SESSION | bool | No | Reject SequenceReset resends that omit `OrigSendingTime` (default: `Y`) |
| `CmeEnhancedResend` | SESSION | bool | No | Enable CME-style enhanced resend: includes tag 789 in Logout (default: `N`) |

---

## Message Handling

| Setting | Section | Type | Required | Description |
|---|---|---|---|---|
| `CheckLatency` | SESSION | bool | No | Reject messages whose `SendingTime` exceeds `MaxLatency` seconds (default: `Y`) |
| `MaxLatency` | SESSION | int | No | Maximum acceptable message age in seconds when `CheckLatency=Y` (default: `120`) |
| `EnableLastMsgSeqNumProcessed` | SESSION | bool | No | Include tag 369 (`LastMsgSeqNumProcessed`) in all outbound headers (default: `N`) |
| `TimestampPrecision` | SESSION | string | No | Outbound `SendingTime` precision. Valid: `Second`, `Millisecond`, `Microsecond`, `Nanosecond` (default: `Millisecond`) |
| `Encoding` | DEFAULT/SESSION | string | No | Character encoding for message bytes, e.g. `utf-8` |

---

## Socket Options

| Setting | Section | Type | Required | Description |
|---|---|---|---|---|
| `SocketNodelay` | DEFAULT/SESSION | bool | No | Set `TCP_NODELAY` (disable Nagle's algorithm) (default: `Y`) |
| `SocketSendBufferSize` | DEFAULT/SESSION | int | No | TCP send buffer size in bytes |
| `SocketReceiveBufferSize` | DEFAULT/SESSION | int | No | TCP receive buffer size in bytes |
| `SocketSendTimeout` | DEFAULT/SESSION | int | No | Send timeout in milliseconds. `0` or `-1` = infinite |
| `SocketReceiveTimeout` | DEFAULT/SESSION | int | No | Receive timeout in milliseconds. `0` or `-1` = infinite |
| `SocketIgnoreProxy` | DEFAULT/SESSION | bool | No | Bypass system proxy settings (default: `N`) |

---

## SSL / TLS

| Setting | Section | Type | Required | Description |
|---|---|---|---|---|
| `SSLEnable` | DEFAULT/SESSION | bool | No | Enable SSL/TLS. Auto-enabled when `SSLCertificate` is set |
| `SSLServerName` | DEFAULT/SESSION | string | No | Expected server certificate CN or DNS SAN for validation |
| `SSLCertificate` | DEFAULT/SESSION | string | No | Path to PFX certificate file, or cert store name. A `..\` prefix resolves relative to the application base directory |
| `SSLCertificatePassword` | DEFAULT/SESSION | string | No | Password for a PFX certificate file |
| `SSLValidateCertificates` | DEFAULT/SESSION | bool | No | Validate the peer certificate chain (default: `Y`) |
| `SSLCheckCertificateRevocation` | DEFAULT/SESSION | bool | No | Check CRL/OCSP revocation. Requires `SSLValidateCertificates=Y` (default: `Y`) |
| `SSLProtocols` | DEFAULT/SESSION | string | No | TLS version. Corresponds to .NET `SslProtocols` enum, e.g. `Tls12`, `Tls13`, `Default` |
| `SSLCACertificate` | DEFAULT/SESSION | string | No | Acceptor only. Path to CA certificate (`.cer`) used to validate client certificates |
| `SSLRequireClientCertificate` | DEFAULT/SESSION | bool | No | Acceptor only. Require a client certificate on incoming connections (default: `Y`) |

---

## Logging

| Setting | Section | Type | Required | Description |
|---|---|---|---|---|
| `ScreenLogShowIncoming` | SESSION | bool | No | Write incoming messages to the console log (default: `N`) |
| `ScreenLogShowOutgoing` | SESSION | bool | No | Write outgoing messages to the console log (default: `N`) |
| `ScreenLogShowEvents` | SESSION | bool | No | Write session lifecycle events to the console log (default: `N`) |

---

## Field Redaction

| Setting | Section | Type | Required | Description |
|---|---|---|---|---|
| `RedactFieldsInLogs` | SESSION | int list | No | Comma-separated FIX tag numbers to redact in all log output, e.g. `108,554,95,96` |
| `RedactionLogText` | SESSION | string | No | Replacement text for redacted field values (default: `<redacted>`) |

---

## Authentication

| Setting | Section | Type | Required | Description |
|---|---|---|---|---|
| `Password` | SESSION | string | No | **fpsim enhancement.** Session password supplied to the application on logon for counterparty authentication |

---

## Mutual Exclusions & Dependencies

| Setting | Conflict / Dependency |
|---|---|
| `NonStopSession=Y` | Mutually exclusive with `StartTime`, `EndTime`, `StartDay`, `EndDay`, `Weekdays` |
| `UseLocalTime=Y` | Mutually exclusive with `TimeZone` |
| `StartDay` / `EndDay` | Mutually exclusive with `Weekdays`; both fields required together |
| `TransportDataDictionary` | FIXT sessions only; requires `UseDataDictionary=Y` |
| `AppDataDictionary` | FIXT sessions only; requires `UseDataDictionary=Y` |
| `DefaultApplVerID` | Required when `BeginString=FIXT.1.1` |
| `SSLCheckCertificateRevocation` | Has no effect when `SSLValidateCertificates=N` |
| `SSLRequireClientCertificate` | Acceptor only; ignored on initiator sessions |
| `SSLCACertificate` | Acceptor only; ignored on initiator sessions |
| `SocketAcceptHost` / `SocketAcceptPort` | Acceptor only |
| `SocketConnectHost` / `SocketConnectPort` | Initiator only |
| `HeartBtInt` | Required for initiator; ignored for acceptor (acceptor echoes the initiator's value) |
| `SessionQualifier` | Initiator only |

---

## Minimal Configuration Examples

### Initiator

```
[DEFAULT]
ConnectionType=initiator
ReconnectInterval=30
FileStorePath=./store
FileLogPath=./log
SenderCompID=CLIENT

[SESSION]
BeginString=FIX.4.2
TargetCompID=SERVER
SocketConnectHost=localhost
SocketConnectPort=5001
HeartBtInt=30
StartTime=00:00:00
EndTime=00:00:00
```

### Acceptor

```
[DEFAULT]
ConnectionType=acceptor
FileStorePath=./store
FileLogPath=./log

[SESSION]
BeginString=FIX.4.2
SenderCompID=SERVER
TargetCompID=CLIENT
SocketAcceptPort=5001
StartTime=00:00:00
EndTime=00:00:00
```
