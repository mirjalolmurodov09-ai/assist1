# Xavfsizlik modeli

Maqsad: faqat ruxsat etilgan lokal sinf tarmog‘ida, ochiq (o‘quvchi ko‘rib turadigan) va autentifikatsiyalangan boshqaruv. Yashirin backdoor, internet orqali boshqaruv, autentifikatsiyasiz buyruq **yo‘q**.

## Himoya qatlamlari
| Qatlam | Qanday |
|---|---|
| Tarmoq chegarasi | Ikkala tomon faqat shaxsiy/lokal manzillarni qabul qiladi (10/8, 172.16/12, 192.168/16, 169.254/16). Public IP rad etiladi. Firewall qoidasi: dastur+port+LocalSubnet. |
| Discovery | UDP paketda sir yo‘q. Javob HMAC‑SHA256 bilan imzolanadi (kalit = sinf kodidan PBKDF2‑SHA256, 100k), so‘rov nonce’iga bog‘langan, vaqt tekshiriladi, manba lokal subnetda bo‘lishi shart. Noma’lum/boshqa sinf Teacher’iga ulanilmaydi. |
| Kanal | TLS 1.2/1.3. Teacher o‘zi imzolagan sertifikat ishlatadi: Student birinchi muvaffaqiyatli autentifikatsiyadan keyin **sertifikat barmoq izini pin qiladi**; o‘zgarsa ulanish rad etiladi. |
| O‘zaro autentifikatsiya | Challenge–response: ikki tomon ham sinf kodini bilishini isbotlaydi (HMAC), isbotlar TLS sertifikat barmoq izi, ikki nonce, deviceId va vaqtga bog‘langan (**channel binding**) — MITM boshqa sertifikat bilan o‘ta olmaydi. Challenge muddati (±2 daqiqa) va replay (bir martalik nonce) tekshiriladi. |
| Sessiya | Har yo‘nalish uchun alohida kalit (uzatilmaydi). Har xabar: sessionId, messageId, ketma‑ket sequence, timestamp, HMAC imzo. Imzo/sessiya/vaqt/sequence/takror xato → sessiya darhol uziladi. Aks ettirish (reflection) himoyasi: yo‘nalish kalitlari farqli. |
| Tasdiqlash | Sinf kodini bilgan noma’lum kompyuter ham **Pending**: buyruq, ekran, kadr qabul qilinmaydi — faqat o‘qituvchi tasdiqlagandan keyin. Student tomonida ham buyruq faqat `Approved` sessiyada bajariladi. |
| Brute‑force | Sinf kodi: 40 bit tasodifiy + yil (Teacher yaratganida). Bitta manzildan 1 daqiqada 10 xato → vaqtincha rad. Foydalanuvchi paroli: PBKDF2‑SHA256 210k, 5 xatodan keyin 1 daqiqa blok. |
| Maxfiy ma’lumot | Sinf kodi diskda DPAPI (joriy Windows foydalanuvchisi) bilan shifrlangan (Teacher’da DB’da, Student’da settings.json’da). Parollar faqat hash. TLS PFX paroli DPAPI bilan. Loglarda `password/token/secret/private key/sinf kodi` niqoblanadi. |
| Buyruq ruxsati | 1) autentifikatsiyalangan sessiya, 2) sessionId, 3) imzo, 4) vaqt/sequence, 5) qurilma tasdiqlangan, buyruq ma’lum va yoqilgan. Student `appsettings.json → Agent:EnabledCommands` bilan har bir buyruqni o‘chira oladi. Parametrlar uzunlik/format bo‘yicha tekshiriladi (jarayon nomi, fayl yo‘li, kenglik, FPS, kechikish). |
| Shaffoflik | Ekran kuzatuvi, bloklash, masofadan boshqaruv, o‘qituvchi ekrani — o‘quvchi oynasida va tray’da ko‘rinadi; boshqaruv paytida qizil chiziq. Teacher ulanishi uzilsa hammasi to‘xtaydi; blok 30 s da (sozlanadi) o‘zi ochiladi. |
| Imtiyoz | Ikkala dastur `asInvoker`. Admin faqat installerda. Student startup — HKCU Run (foydalanuvchi ko‘radi va o‘chira oladi). Yashirin jarayon, watchdog, o‘zini qayta tiklash **yo‘q**. |

## Qabul qilingan cheklovlar
- Sinf kodini bilgan kishi o‘z kompyuterini "Pending" qila oladi, lekin tasdiqsiz hech narsa qila olmaydi. Kod oshkor bo‘lsa — yangisini yarating (barcha ulanishlar uziladi).
- Lock ekranida Ctrl+Alt+Del va Task Manager ishlaydi (Windows xavfsizligini chetlab o‘tilmaydi); o‘quvchi agentni Task Manager’dan tugatishi mumkin — Teacher kompyuterni "Oflayn" deb ko‘radi.
- UAC/Secure desktop va administrator oynalari ko‘rinmaydi/boshqarilmaydi (Windows cheklovi).
- Bir nechta monitor: hozir faqat asosiy monitor olinadi.

## Testlar (avtomatik)
`tests/` ichida: noto‘g‘ri/muddati o‘tgan/takror challenge, boshqa sertifikat (MITM), imzo buzish, takror xabar, sessiya almashtirish, eskirgan vaqt, aks ettirish, xom mijoz bilan brute‑force va throttle, Pending qurilma buyruq/kadr yubora olmasligi, parol hash/bloklash/oxirgi admin, loglarda sir yo‘qligi, sinf kodi DB’da ochiq yozilmagani, ommaviy manzillar rad etilishi, buyruq parametrlari validatsiyasi.
