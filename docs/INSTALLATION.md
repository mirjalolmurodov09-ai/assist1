# O‘rnatish qo‘llanmasi

## Talablar
- Windows 10 yoki 11 (x64). .NET alohida o‘rnatilmaydi (self-contained).
- O‘qituvchi va o‘quvchi kompyuterlari **bitta lokal tarmoqda** (LAN yoki bir xil Wi‑Fi). Internet kerak emas.
- O‘rnatish uchun administrator huquqi (Program Files va firewall qoidasi uchun). Dasturlarning o‘zi oddiy foydalanuvchi sifatida ishlaydi.

## 1. O‘qituvchi kompyuteri
1. `ClassroomControl-Teacher-Setup.exe` ni ishga tushiring. Installer faqat **lokal tarmoq** uchun firewall qoidalarini qo‘shadi: UDP 39500 va TCP 39501.
2. Dasturni oching. Birinchi ishga tushirishda **administrator** hisobini yarating (parol kamida 8 belgi).
3. **Sozlamalar → Umumiy** bo‘limida **Sinf kodi** (masalan `CLASS-7KQ2M9XD-2026`) ko‘rinadi. Uni o‘quvchilarga bering (doskaga yozing).

## 2. Har bir o‘quvchi kompyuteri
1. `ClassroomControl-Student-Setup.exe` ni ishga tushiring ("Windows ishga tushganda boshlash" ni tanlash mumkin).
2. Student Agent ochiladi → **Connect** → sinf kodini va ismni kiriting.
3. O‘qituvchi dasturida banner chiqadi: "N ta kompyuter tasdiqni kutmoqda" → **Ko‘rish → Tasdiqlash**.
4. Tasdiqlangandan keyin o‘quvchida 🟢 Connected / Registered ko‘rinadi.

### Ko‘p kompyuterga o‘rnatish (jim rejim)
```
ClassroomControl-Student-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=startup
"C:\Program Files\ClassroomControl\StudentAgent\ClassroomControl.Student.exe" --classroom-code CLASS-XXXX-YYYY --student-name "Ali" 
```
`--classroom-code`, `--teacher-address`, `--teacher-port`, `--discovery-port`, `--student-name`, `--computer-name` kalitlari sozlamalarni yozadi (xuddi Sozlamalar oynasidagi tekshiruvlar bilan), kod DPAPI bilan shifrlangan holda saqlanadi.

## 3. To‘liq (ikkalasi) installer
`ClassroomControl-Setup.exe` — komponentlarni tanlash mumkin (Teacher / Student Agent).

## O‘chirish
Windows → Sozlamalar → Ilovalar → "Classroom Control …" → O‘chirish. Dastur fayllari, yorliqlar, startup yozuvi va firewall qoidalari olib tashlanadi. Oxirida **konfiguratsiya va loglarni saqlab qolish yoki o‘chirish** so‘raladi (jim rejimda `/DELETEUSERDATA` kaliti bilan o‘chiriladi).

## Fayllar qayerda
| Nima | Joy |
|---|---|
| Dastur | `C:\Program Files\ClassroomControl\…` |
| Student sozlamalari, loglar | `%LOCALAPPDATA%\ClassroomControl\StudentAgent\` |
| Teacher bazasi (`classroom.db`), sertifikat, skrinshotlar, loglar | `%LOCALAPPDATA%\ClassroomControl\Teacher\` |

## Administrator huquqi kerak bo‘ladigan amallar (hammasi shu)
1. Installer: `Program Files` ga nusxalash.
2. Installer/uninstaller: Windows Firewall qoidasini qo‘shish/o‘chirish (`netsh advfirewall`).
Qolgan hamma narsa (ishlatish, startup, sozlamalar, restart/shutdown buyruqlari) oddiy foydalanuvchi huquqida ishlaydi.
