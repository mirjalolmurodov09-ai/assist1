# Testlar va qabul mezonlari

## 1. Avtomatik testlar (`dotnet test tests/ClassroomControl.Tests`, ~250 ta)
Barchasi **haqiqiy** TLS, UDP, SQLite va haqiqiy Student/Teacher mantig‘i bilan; faqat Windows (ekran, sichqoncha, shutdown, bloklash oynasi) soxta (`TestKit/FakePlatform`).

| № | Talab etilgan test | Qaysi test qoplaydi |
|---|---|---|
| 1 | Teacher + 1 Student | `IntegrationTests.Full_classroom_scenario`, `ConnectionTests.*` |
| 2 | Teacher + 5 Student | `FeatureIntegrationTests.Lock_all_locks_every_connected_computer_at_once` |
| 3 | Teacher + 16 Student | `IntegrationTests.Sixteen_agents_in_a_simulated_classroom…` · `tools/AgentSimulator` |
| 4 | Tarmoq uzilishi | `ConnectionTests.Teacher_going_away_causes_offline…`, `IntegrationTests.Silent_teacher_triggers_heartbeat_timeout…` |
| 5 | Tarmoq tiklanishi | shu testlar (qayta ulanish) + `Network_change_closes_the_connection_and_restarts_discovery` |
| 6 | Teacher restart | `ServerBehaviourTests.Approval_survives_a_teacher_restart`, `TeacherAppTests.Runtime_…keeps_the_code_across_restarts` |
| 7 | Student restart | `ServerBehaviourTests.A_restarted_student_keeps_its_identity_and_approval`, `SettingsTests.Device_id_is_created_once…` |
| 8 | Masofadan boshqarish | `FeatureIntegrationTests.Remote_control…`, `RemoteInputServiceTests`, `TeacherAppTests.Full_screen_remote_control…` |
| 9 | Lock/Unlock | `FeatureIntegrationTests.Lock_*`, avto-ochilish testlari |
| 10 | Screenshot | `FeatureIntegrationTests.Screenshot_*`, `TeacherAppTests.Screenshot_history…` |
| 11 | Teacher ekranini uzatish | `FeatureIntegrationTests.Teacher_screen…`, `TeacherAppTests.Teacher_screen_sharing…` |
| 12 | Xabar yuborish (hammaga) | `FeatureIntegrationTests.Messages…`, `TeacherAppTests.A_message_to_everyone…` |
| 13 | Restart/Shutdown | `FeatureIntegrationTests.Restart_and_shutdown…`, `TeacherAppTests.Restart_and_shutdown_ask_for_confirmation_first` |
| 14 | Noto‘g‘ri autentifikatsiya | `ConnectionTests.Wrong_classroom_code…`, `AuthenticationTests.*`, `ServerBehaviourTests.Client_without_the_classroom_code…` |
| 15 | Noma’lum qurilma | `ServerBehaviourTests.Unknown_computer_stays_pending…`, `CommandTests.Commands_are_refused_until…` |

### Xavfsizlik testlari (37-band)
Authentication bypass: `ServerBehaviourTests`, `AuthenticationTests`. Noma’lum qurilma buyruq yubora olmaydi: `Unknown_computer…`, `CommandTests`. Replay: `AuthenticationTests.Replayed_*`, `IntegrationTests.Replay_attack…`. Sessiya kaliti uzatilmaydi / xotirada: `HandshakeCrypto` (faqat HMAC hosilalari). Parollar hash: `UserTests.Passwords_are_hashed…`. Loglarda sir yo‘q: `RedactorTests`, `ServerBehaviourTests.Client_without…`. Sinf kodi diskda ochiq emas: `SettingsTests.Classroom_code_is_never_stored…`, `ServerBehaviourTests.Classroom_code_is_not_stored…`. Shifrlangan kanal: TLS majburiy (`ConnectionService`, `ClassroomServer`), pin: `ConnectionTests.Changed_teacher_certificate…`.

## 2. Windows CI (har push’da, `.github/workflows/build.yml`)
Build (warning = error) → testlar → self-contained publish (Teacher + Student) → **Student smoke test** → **Teacher UI + 2 ta haqiqiy Student (biri discovery, biri to‘g‘ridan) bilan end‑to‑end**: ping, device info, *haqiqiy GDI skrinshot + JPEG*, monitoring kadrlari, xabar oynasi, bloklash/ochish, *haqiqiy SendInput*, o‘qituvchi ekrani, *haqiqiy `notepad.exe` ochish/yopish*, CPU/RAM; 4 ta tema/til variantida oyna rasmlari (`ci-ui` branch) → 3 ta installer → o‘rnatish/o‘chirish testi (fayllar, yorliqlar, startup, 3 ta firewall qoidasi LocalSubnet bilan, tozalash).

## 3. Qo‘lda tekshiriladigan (CI qila olmaydi)
Toza Windows 10/11 + haqiqiy tarmoq:
1. Installer → dastur ishga tushadi → Device ID yaratiladi (`settings.json`).
2. Windows restart → Student avtomatik ishga tushadi (tray).
3. Teacher + ≥2 o‘quvchi kompyuter, Wi‑Fi/LAN: discovery, tasdiqlash, 🟢 Connected, heartbeat.
4. LAN kabelini uzib/ulab, Wi‑Fi almashtirib: 🔴 → 🟢, yangi IP, qayta discovery.
5. Bloklash oynasi barcha monitorlarni yopadi; Win/Alt+Tab bloklanadi; Ctrl+Alt+Del ishlaydi; Teacher o‘chirilsa 30 s da ochiladi.
6. Masofadan boshqarish: bir nechta monitor, yuqori DPI, administrator oynalari (boshqarilmasligi normal).
7. 16 ta haqiqiy kompyuter bilan yuklama (CPU/trafik) — `Sozlamalar → Kuzatuv`.
8. Uninstall: "config/log saqlansinmi?" dialogi.

## 4. Ma’lum cheklovlar (halol ro‘yxat)
- Bir nechta monitorli o‘quvchi kompyuterida faqat asosiy monitor olinadi.
- Teacher ekrani har o‘quvchiga alohida yuboriladi (multicast yo‘q): katta sinfda FPS/kenglikni kamaytiring.
- Differensial yangilash — o‘zgargan plitkalar chegara to‘rtburchagi (har plitka alohida emas).
- Lock/UAC/secure desktop ekrani olinmaydi; administrator oynalari masofadan boshqarilmaydi (UIPI).
- ARM64/x86 uchun alohida `dotnet publish -r win-arm64|win-x86` kerak; faqat x64 sinovdan o‘tgan.
