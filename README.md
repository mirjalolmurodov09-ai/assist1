# Classroom Control

Lokal tarmoq (LAN/Wi‑Fi) orqali o‘qituvchi kompyuteridan o‘quvchilar kompyuterlarini kuzatish va boshqarish dasturi (Windows 10/11, internet kerak emas).

| Qism | Nima qiladi | Chiqish fayli |
|---|---|---|
| **Teacher** | Kompyuterlarni topadi, ekranlarini ko‘rsatadi, boshqaradi, bloklaydi, xabar yuboradi, skrinshot oladi, o‘z ekranini uzatadi | `ClassroomControl.Teacher.exe` |
| **Student Agent** | Har bir o‘quvchi kompyuterida ishlaydi, faqat tasdiqlangan Teacher buyruqlarini bajaradi, nima bo‘layotganini o‘quvchiga ko‘rsatib turadi | `ClassroomControl.Student.exe` |
| **Local Classroom Server** | Teacher ichidagi komponent: TLS/UDP server, SQLite, autentifikatsiya, tasdiqlash, audit log | `ClassroomControl.ClassroomServer.dll` |

Installerlar: `ClassroomControl-Teacher-Setup.exe`, `ClassroomControl-Student-Setup.exe`, `ClassroomControl-Setup.exe` (ikkalasi).


## Downloads (built and tested by GitHub Actions on Windows)

Files are on the `dist` branch: `ClassroomControl-Setup.exe` (Teacher + Student), `ClassroomControl-Teacher-Setup.exe`,
`ClassroomControl-Student-Setup.exe`, and portable `ClassroomControl.Teacher-win-x64.zip` / `ClassroomControl.Student-win-x64.zip`.
Open the `dist` branch on GitHub, select a file and press *Download raw file*.

## Hujjatlar
- [O‘rnatish qo‘llanmasi](docs/INSTALLATION.md) · [Foydalanuvchi qo‘llanmasi (o‘qituvchi va o‘quvchi)](docs/USER_MANUAL.md) · [Tarmoq va firewall](docs/NETWORK.md)
- [Xavfsizlik modeli](docs/SECURITY.md) · [Protokol 1.0](docs/PROTOCOL.md) · [Ma’lumotlar bazasi](docs/DATABASE.md) · [Testlar va qabul mezonlari](docs/TESTING.md)

## Tuzilma
```
src/Shared            protokol, kriptografiya, xabarlar, MVVM asoslari, ekran primitivlari (net8.0)
src/Infrastructure    log, DPAPI/AES maxfiy ma’lumot himoyasi, yo‘llar
src/ClassroomServer   Teacher serveri: TLS+UDP, SQLite, foydalanuvchilar, tasdiqlash, buyruqlar, audit
src/StudentAgent.Core Student mantig‘i: discovery, autentifikatsiya, heartbeat, reconnect, buyruq handlerlari, ViewModel
src/TeacherApp.Core   Teacher mantig‘i: ViewModel, monitoring, ekran ulashish, lokalizatsiya (uz/en/ru)
src/Platform          Windows: GDI ekran olish, JPEG, SendInput, shutdown/jarayon boshqaruvi, klaviatura bloklash
src/Wpf               umumiy WPF: kadr kompozitori
src/StudentAgent      Student WPF ilovasi (oynalar, tray, bloklash oynasi, startup)
src/TeacherApp        Teacher WPF ilovasi (Light/Dark, 3 til, to‘liq ekran + masofaviy boshqaruv)
src/TestKit           haqiqiy server + soxta Windows bilan sinov uchun jihozlar
tests/                unit va integration testlar (haqiqiy TLS, UDP, SQLite)
tools/AgentSimulator  16 ta (yoki N ta) Student Agentni simulyatsiya qiladi
installer/            Inno Setup skripti (3 nashr) · build/ yig‘ish skriptlari · docs/
```

## Yig‘ish va test
```
dotnet test tests/ClassroomControl.Tests                       # istalgan OS (Linux/Windows), ~250 test
dotnet run --project tools/AgentSimulator -- mock --code CLASS-8F4K-2026   # 16 ta soxta o‘quvchi
build\publish.ps1 -Installer                                    # Windows: self-contained x64 + 3 installer
```
GitHub Actions (`.github/workflows/build.yml`) Windows’da hammasini quradi, testlarni ishga tushiradi, **haqiqiy Teacher + 2 ta haqiqiy Student**ni bir-biriga ulab barcha funksiyalarni tekshiradi, installerlarni o‘rnatib/o‘chirib ko‘radi.
