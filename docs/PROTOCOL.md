# Classroom Control — Student Agent protocol 1.0

All traffic stays on the local classroom network. The Student Agent never accepts inbound connections.

| Purpose | Transport | Port (default, configurable) |
|---|---|---|
| Discovery | UDP broadcast request → unicast response | 39500 |
| Session | TCP + TLS 1.2/1.3 (Teacher is the server) | 39501 (announced by discovery) |

## Discovery (no secrets in packets)
Student → subnet broadcast: `{service:"ClassroomControl", version:1, type:"DISCOVERY_REQUEST", device:"STUDENT", nonce, timestamp}`.
Teacher → Student (unicast): `DISCOVERY_RESPONSE`, `device:"TEACHER"`, `port`, `classroom`, `teacherId`, `teacherName`, echoed `nonce`, `timestamp`, `signature`.

`signature = HMAC-SHA256(classroomKey, fields)`, `classroomKey = PBKDF2-SHA256(classroom code, fixed salt, 100000 it.)`.
The Student accepts a response only if: service/version/type/device match, the nonce equals its own request, the timestamp is fresh,
the source address is on a local subnet (private range), and the signature verifies. Responses from a Teacher with a different
classroom code fail the signature and are ignored (reported as "Classroom code noto‘g‘ri.").

## Framing
4-byte big-endian length + UTF-8 JSON `WireMessage` (`protocolVersion, type, sessionId, messageId, sequence, timestamp, payload(string), signature`). Max 256 KiB.

## Authentication (inside TLS)
1. Student → `HELLO {deviceId, agentVersion, protocolVersion, computerName, nonceS, timestamp}`
2. Teacher → `CHALLENGE {protocolVersion, teacherId, teacherName, classroomId, classroomName, nonceT, timestamp, serverProof}`
   `serverProof = HMAC(classroomKey, "SRV1", deviceId, nonceS, nonceT, certFingerprint, timestamp, teacherId, classroomId)`
   Student verifies version, freshness, replay, and the proof (= Teacher identity + classroom code + TLS channel binding).
3. Student → `AUTH_RESPONSE {deviceId, clientProof, computerName, studentName, localIp, os, agentVersion}`
   `clientProof = HMAC(classroomKey, "CLI1", deviceId, nonceT, nonceS, certFingerprint, timestamp)`
4. Teacher → `AUTH_RESULT {success, errorCode, sessionId, registration: Pending|Approved|Rejected, policy, resultProof}`

`certFingerprint` is the SHA-256 of the Teacher's TLS certificate as seen by each side, so a man-in-the-middle with another certificate cannot relay the handshake. The Teacher certificate is also pinned on first successful authentication (Settings → Security shows the status; reset it there if the Teacher PC is reinstalled).

Session keys (never transmitted): `HMAC(classroomKey, "SESSION-C2T"|"SESSION-T2C", nonceS, nonceT, sessionId)` — one key per direction.

## Session messages
Every message after authentication is signed with the direction key over `version, type, sessionId, messageId, sequence, timestamp, payload`.
Receiver rejects: wrong session, bad signature, timestamp outside ±2 min, sequence not strictly increasing, repeated messageId. Any violation ends the session.

| Type | Direction | Notes |
|---|---|---|
| `HEARTBEAT` `{deviceId, sessionId, timestamp, status}` | S→T every 5 s | Teacher answers `HEARTBEAT_ACK`; 15 s of silence = connection lost |
| `REGISTRATION_UPDATE` `{registration, message, policy}` | T→S | approve / reject |
| `COMMAND` `{commandId, name, parameters}` | T→S | |
| `COMMAND_RESPONSE` `{commandId, deviceId, status, timestamp, errorCode, message, payload}` | S→T | |
| `DISCONNECT` `{reason}` | both | |

## Commands
Implemented: `Ping`, `GetStatus`, `GetDeviceInfo`. Reserved (registered, never executed, answer `NotImplemented`):
Lock, Unlock, Screenshot, StartApplication, StopApplication, Restart, Shutdown, StartScreenStream, StopScreenStream, StartRemoteControl, StopRemoteControl.
A command runs only if: it arrived on the authenticated session (id, signature, timestamp, sequence verified), the device is `Approved`, the command is known, implemented and enabled.

## Versioning
Same major version = compatible, the lower minor is negotiated. A different major gives:
"Teacher protocol versiyasi (X) bu Student Agent (1.0) bilan mos emas. Dasturni yangilang."

## Connection states
`Disconnected → Discovering → TeacherFound → Connecting → Authenticating → WaitingForApproval → Connected → Reconnecting → Disconnected`
(shortcuts: Authenticating→Connected when already approved; any state→Disconnected; Reconnecting→Discovering). Invalid transitions throw.
Reconnect back-off: 1, 2, 5, 10, 20, 30 s, then every 30 s without limit. Network change → connection closed, discovery restarted immediately.
