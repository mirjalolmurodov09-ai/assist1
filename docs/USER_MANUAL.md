# Foydalanuvchi qo‘llanmasi

## A. O‘qituvchi (Teacher)

### Kirish
Dasturni oching → foydalanuvchi nomi va parol. Birinchi marta administrator hisobi yaratiladi. 5 marta xato kiritilsa, hisob 1 daqiqaga bloklanadi. Barcha amallar kim tomonidan bajarilgani logga yoziladi.

### Asosiy oyna
- **Yuqori panel:** sinf nomi, kompyuterlar soni, onlayn 🟢 / oflayn 🔴 soni, ustoz; tugmalar: *Hammasini bloklash / ochish*, *Hammaga xabar*, *Ekranimni ko‘rsatish*, *Kuzatuv ⏯* (kichik ko‘rinishlarni to‘xtatish/davom ettirish), *Kompyuter qo‘shish*, *Skrinshotlar*, *Sozlamalar*, *Haqida*.
- **Chap panel:** Barcha / Onlayn / Oflayn / Tasdiq kutayotganlar va sizning guruhlaringiz (1-guruh, Python, Web …) — bosing, shu guruh ko‘rinadi.
- **Markaz:** har bir kompyuter kartasi: ekran ko‘rinishi, nomi, o‘quvchi, IP, holat, CPU, RAM, ping va aloqa sifati (A’lo / Yaxshi / Sust). 🔒 — bloklangan, 🖱 — masofadan boshqarilmoqda.
- **O‘ng panel:** tanlangan kompyuter(lar) uchun amallar. Karta ustiga bosing — tanlash; **Ctrl+bosish** — qo‘shib tanlash; **ikki marta bosish** — to‘liq ekran.

### Yangi kompyuter qo‘shish
O‘quvchi sinf kodini kiritgach kompyuter **Tasdiq kutayotganlar** ro‘yxatiga tushadi (tepada banner ham chiqadi). *Kompyuter qo‘shish* oynasida **Tasdiqlash** yoki **Rad etish**. Tasdiqlanmagan kompyuterga buyruq yuborib bo‘lmaydi, ekrani ko‘rinmaydi.

### Amallar
| Amal | Tavsif |
|---|---|
| Ekranni ko‘rish | to‘liq ekran oynasi, real vaqtga yaqin (sozlanadigan FPS/sifat) |
| Masofadan boshqarish | to‘liq ekran oynasida sichqoncha va klaviatura o‘quvchi kompyuteriga uzatiladi. Yuqorida qizil **"MASOFADAN BOSHQARUV FAOL"**. O‘quvchi ekranida ham qizil chiziq chiqadi. **Ctrl+Shift+F12** — darhol o‘chirish. (Administrator huquqidagi oynalarni Windows boshqarishga ruxsat bermaydi.) |
| Bloklash / Blokni olish | qora ekranda siz yozgan xabar (default: "Diqqat! O‘qituvchi kompyuterni vaqtincha blokladi."). Aloqa uzilsa 30 soniyadan keyin avtomatik ochiladi (Sozlamalar → Xavfsizlik) |
| Xabar yuborish | tanlanganlarga yoki hammaga; o‘quvchida ustida turuvchi oyna chiqadi |
| Skrinshot | `OquvchiIsmi_KompyuterNomi_YYYY-MM-DD_HH-mm-ss.jpg` nomi bilan saqlanadi; **Skrinshotlar** tarixida ochish/o‘chirish |
| Ekranimni ko‘rsatish | tanlanganlarga / guruhga / hammaga to‘liq ekranda. Keyin ulangan kompyuterlar ham avtomatik qo‘shiladi. Qayta bosing — to‘xtaydi |
| Dastur ochish / yopish | masalan `notepad`, `calc` yoki fayl yo‘li; yopish uchun jarayon nomi (`notepad`) |
| Qayta ishga tushirish / O‘chirish | oldin **tasdiq so‘raladi**; o‘quvchi Windows’ning 10 soniyalik ogohlantirishini ko‘radi |
| Nom berish, Guruhga ko‘chirish, Ro‘yxatdan o‘chirish | kompyuterlarni boshqarish |

### Sozlamalar
*Umumiy* (sinf nomi, **sinf kodi** — yangilash/qo‘lda kiritish, til: O‘zbekcha/English/Русский, qorong‘i rejim), *Tarmoq* (portlar, lokal IP), *Xavfsizlik* (Agent faol bo‘lishi, avtomatik blok ochish, Teacher sertifikati, foydalanuvchilar), *Kuzatuv*, *Ekran sifati*, *Guruhlar*, *Student Agentlar*, *Loglar* (qidiruv bilan).

## B. O‘quvchi (Student Agent)
1. Dastur o‘zi ishga tushadi (tray’da yashil/sariq/qizil nuqta). Birinchi marta **sinf kodi** va ismingizni kiriting → **Connect**.
2. Holatlar: 🟡 *Connecting…* → "Waiting for Teacher approval" → 🟢 *Connected / Registered*. 🔴 *Offline* bo‘lsa dastur o‘zi qayta urinadi (1, 2, 5, 10, 20, 30 soniya …).
3. **Dastur nima qilayotgani doim ko‘rinadi:** asosiy oynada "🔒 bloklangan", "👁 ekraningiz kuzatilmoqda", "🖱 kompyuteringiz boshqarilmoqda", "📺 o‘qituvchi ekrani"; boshqarish paytida ekran tepasida qizil chiziq.
4. Tray menyusi: Open, Connection Status, Settings, About, Exit. Oyna yopilsa dastur tray’da qoladi. O‘qituvchi "Agent faol bo‘lsin" siyosatini yoqqan bo‘lsa, Exit uchun sinf kodi so‘raladi (tasodifan yopishdan himoya; Task Manager va o‘chirish baribir ishlaydi).
5. Sozlamalar: kompyuter nomi, ism, sinf kodi, "Start with Windows", Teacher IP/port, discovery port, timeout, Device ID, sertifikat holati.

## C. Maxfiylik
Dastur **ovoz yozmaydi, kamera/mikrofonni yoqmaydi, brauzer parollari yoki shaxsiy fayllarni o‘qimaydi, clipboardni kuzatmaydi**. Faqat sinf boshqaruvi uchun: ekran (o‘qituvchi kuzatganda), kompyuter nomi/IP/CPU/RAM, ulanish holati. Ekran kuzatuvi va masofadan boshqaruv o‘quvchiga ko‘rsatib turiladi.

## D. Muammolar
Qarang: [tarmoq va firewall](NETWORK.md#muammolarni-hal-qilish).
