# Ma’lumotlar bazasi (SQLite)

Teacher: `%LOCALAPPDATA%\ClassroomControl\Teacher\classroom.db` (WAL rejimi, `Microsoft.Data.Sqlite`).

| Jadval | Mazmuni |
|---|---|
| `Users` | foydalanuvchi, PBKDF2 hash, rol (Admin/Teacher), oxirgi kirish, bloklangan |
| `Classrooms` | sinf, **shifrlangan** sinf kodi (DPAPI), teacherId |
| `Groups` | sinf guruhlari (`UNIQUE(ClassroomId, Name)`) |
| `Students` | hisobotlangan o‘quvchi ismlari |
| `Computers` | DeviceId, kompyuter/ko‘rsatiladigan nom, o‘quvchi, IP, MAC, CPU, RAM, guruh, holat (Pending/Approved/Rejected), LastSeen |
| `Sessions` | har bir ulanish: sessionId, vaqtlar, uzilish sababi |
| `Commands` | yuborilgan buyruqlar: id, nom, natija, kim so‘ragan |
| `Logs` | audit: vaqt, ustoz, kompyuter, amal, natija, tafsilot |
| `Screenshots` | skrinshot tarixi (fayl yo‘li) |
| `Settings` | kalit/qiymat (FPS, sifat, til, tema, portlar, TLS PFX paroli (shifrlangan)…) |

## Migratsiya
`SchemaVersion` jadvali joriy versiyani saqlaydi. `ClassroomStore` ochilganda `Data/Schema.cs` dagi yuqori versiyali skriptlar **bitta tranzaksiyada** ketma-ket bajariladi. Mavjud migratsiyani o‘zgartirmang — yangi versiya qo‘shing:
```csharp
(2, "ALTER TABLE Computers ADD COLUMN Notes TEXT NOT NULL DEFAULT '';"),
```
Testlar (`StoreTests`) barcha jadvallar yaratilishini va qayta ochish xavfsizligini tekshiradi.

## Zaxira
`classroom.db` (+ `-wal`, `-shm`) va `teacher-tls.pfx` ni nusxalang. Sertifikat yo‘qolsa o‘quvchilar "sertifikat o‘zgardi" deb ulanmaydi (qayta o‘rnatish tugmasi bor).
