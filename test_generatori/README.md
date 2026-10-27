# Test generatori

O'qituvchilar uchun desktop dastur: **Word (.docx) yoki Excel (.xlsx)** dagi test savollaridan har bir
o'quvchiga alohida variant tuzadi, hammasini **bitta chop etishga tayyor PDF** qiladi va keyin
javoblarni **Test ID** bo'yicha tekshiradi.

## O'rnatish va ishga tushirish

```bash
pip install -r requirements.txt
python main.py
```
Linux'da Tkinter alohida o'rnatiladi: `sudo apt install python3-tk`.
Python 3.10 yoki yangiroq kerak. Windows va macOS'da Tkinter Python bilan birga keladi.

## Savollar fayli formati

**Word (.docx)** — to'g'ri javobning yoniga (oxiriga yoki boshiga) `+` qo'yiladi:
```
1. Oʻzbekiston poytaxti qayer?
A) Samarqand
B) Toshkent +
C) Buxoro
D) Xiva
```
* Variant belgisi: `A)` `A.` `A:` `(A)` `a)`, lotin yoki kirill (А В С Д / А Б В Г) harflarida.
* Savol raqami: `1.` `1)` `1-`. Raqamsiz (Word avto-raqamlash) bo'lsa ham ishlaydi: 4 ta variantdan keyingi matn yangi savol hisoblanadi.
* Variantlar bitta qatorda bo'lishi mumkin: `A) 3 B) 4+ C) 5 D) 6`. Savol va variantlar jadval ichida ham o'qiladi.
* Jadvalda `Savol | A | B | C | D` ustunlari bo'lsa, u Excel kabi o'qiladi.

**Excel (.xlsx)** — birinchi varaq: `Savol | A | B | C | D`; to'g'ri javob yacheykasi oxirida `+`.
Sarlavha qatori avtomatik aniqlanadi. Ixtiyoriy «To'g'ri javob» ustuni (A/B/C/D) ham qo'llab-quvvatlanadi.

Fayl o'qilgach, xatolar (variantlar soni ≠ 4, `+` yo'q yoki bir nechta, bo'sh matn) **qator/savol raqami bilan**
ro'yxat qilinadi. Takrorlangan savollar ogohlantirish bilan olib tashlanadi. Xatoli savollarni o'tkazib yuborib
davom etish yoki to'xtatish mumkin. `+` belgisi PDF'ga umuman chiqmaydi.

## Ishlash tartibi

1. **Test tuzish** tabi: fayl, o'quvchilar soni (1–1000), har bir testdagi savollar soni, sarlavha, natija papkasi → «TEST TUZISH».
2. Natija: `testlar_<sana-vaqt>.pdf` (har bir o'quvchi yangi sahifadan, har betda **TEST ID**, pastda «ID, bet X / Y»,
   oxirida javoblar varaqasi) va `javoblar_kaliti_<sana-vaqt>.xlsx` (faqat o'qituvchi uchun).
   Mavjud fayllar hech qachon ustiga yozilmaydi.
3. **Tekshirish** tabi: sessiyani tanlang → Test ID kiriting (harf registri va bo'shliq farqi yo'q) →
   javoblarni `ABCDABCD`, `A B C D`, `1A 2B 3C` ko'rinishida yoki tugmalar bilan kiriting → «Tekshirish va saqlash».
   Natija: to'g'ri/noto'g'ri/javobsiz, foiz, baho va xato qilingan savollar. Qayta tekshirilganda yangilashni so'raydi.
   «Natijalarni Excelga eksport» tugmasi `natijalar_*.xlsx` yaratadi.
4. **Sozlamalar** tabi: baholash shkalasi (standart 86/71/56 %) va oldingi sessiyalar (ochish, o'chirish).

Hamma ma'lumot `data/tests.db` (SQLite) da saqlanadi, dastur qayta ochilganda oldingi sessiyalar mavjud bo'ladi.
Loglar: `logs/app.log`.

## Test tuzish qoidalari

* Har bir o'quvchiga bazadan takrorlanmas Q ta savol; savollar ishlatilish soni bo'yicha **tekis taqsimlanadi**.
* Savollar tartibi va variantlar tartibi aralashtiriladi (to'g'ri javob o'z matni bilan ko'chadi).
  «Hammasi to'g'ri», «A va B» kabi tartibga bog'liq variantlari bor savollar aralashtirilmaydi (ogohlantirish chiqadi).
* Ikki o'quvchiga aynan bir xil variant tushmaydi (imkon bo'lmasa, ogohlantirish).
* Test ID: `T-XXXXXX` (A–Z va 2–9, chalkash `O 0 I 1` yo'q). Seed kiritilmasa ID lar barcha oldingi
  sessiyalarga nisbatan ham noyob bo'ladi. Seed kiritilsa natija aynan qayta tiklanadi.
  Dastur ishlatilgan seed'ni ko'rsatadi.

## Testlar

```bash
pip install -r requirements-dev.txt
python -m pytest tests -q          # GUI testlari ekran bo'lmasa (xvfb-run python -m pytest ...) o'tkazib yuboriladi
python examples/make_examples.py   # examples/sample.docx va sample.xlsx (50 savol) qayta yaratish
```

## Windows uchun .exe

`build_exe.bat` ni ishga tushiring, yoki qo'lda:
```bat
pip install -r requirements-dev.txt
python -m PyInstaller --noconfirm --onefile --windowed --name TestGeneratori --add-data "fonts;fonts" main.py
```
Natija: `dist\TestGeneratori.exe`. `data/` va `logs/` papkalari .exe yoniga yoziladi (yozish mumkin
bo'lmasa, uy papkasidagi `TestGeneratori` ga).

## Cheklovlar

* Eski `.doc` / `.xls` formatlar qo'llab-quvvatlanmaydi — Word/Excelda «Boshqacha saqlash» bilan `.docx`/`.xlsx` qiling.
* Rasm, formula (Word tenglamalari) va matn qutilaridagi savollar o'qilmaydi — faqat matn.
* Kirill «В» harfi variant belgisi sifatida joylashuviga qarab B yoki C deb o'qiladi; Excelning «To'g'ri javob»
  ustunida esa B deb olinadi.
* Shrift: DejaVu Sans (`fonts/`), lotin, kirill va o'zbek harflarini qamrab oladi; shriftda yo'q belgi `?` bilan almashtiriladi.
* Ishlash tezligi: 1000 ta o'quvchi × 30 savol ≈ 3000 sahifa, taxminan 40 soniya, ~150 MB xotira.
