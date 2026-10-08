# Tarmoq va firewall

## Portlar
| Kim | Protokol / port | Yo‘nalish | Nima uchun |
|---|---|---|---|
| Teacher | UDP **39500** | kiruvchi, faqat LocalSubnet | o‘quvchilar Teacher’ni qidiradi (broadcast so‘rov, unicast javob) |
| Teacher | TCP **39501** | kiruvchi, faqat LocalSubnet | TLS sessiya (autentifikatsiya, buyruqlar, ekran) |
| Student | UDP (dasturga bog‘langan, istalgan port) | kiruvchi, faqat LocalSubnet | Teacher’ning discovery javobini olish (javob so‘rov manbasining tasodifiy portiga keladi) |
| Student | TCP → Teacher:39501 | chiquvchi | Windows’da chiquvchi trafik odatda ruxsat etilgan |

Installer qoidalarni **dastur + protokol + port + LocalSubnet** bilan cheklab yaratadi. Hech narsa internetga ochilmaydi; Student Agent va Teacher ommaviy (public) IP manzillarni rad etadi. Portlar **Sozlamalar → Tarmoq** da o‘zgartiriladi; o‘zgartirsangiz firewall qoidasini ham yangilang.

## Discovery qanday ishlaydi va chegaralari
- UDP broadcast **router orqali o‘tmaydi**. O‘quvchi va o‘qituvchi bitta subnetda (bitta VLAN) bo‘lishi kerak.
- Wi‑Fi’da "client isolation / AP isolation" yoqilgan bo‘lsa, kompyuterlar bir-birini ko‘rmaydi — o‘chiring.
- Boshqa subnet yoki broadcast ishlamasa: Student **Sozlamalar → Tarmoq → Teacher IP** ga o‘qituvchi IP’sini yozing (faqat shaxsiy diapazon: 10.x, 172.16–31.x, 192.168.x). Autentifikatsiya va TLS baribir ishlaydi.
- Public tarmoq profili ham ishlaydi (qoida `profile=any`, lekin faqat LocalSubnet’ga ochiq).

## Trafik (16 kompyuter, taxminiy)
| Rejim | Sozlama (default) | Taxminiy yuklama |
|---|---|---|
| Kichik ko‘rinishlar | 3 FPS, JPEG 50, 480 px, **faqat o‘zgargan qism** | statik ish stoli ≈ 0; faol ≈ 0.2–1 MB/s jami |
| To‘liq ekran (1 kompyuter) | 15 FPS, JPEG 70, 1280 px | 0.3–1.5 MB/s |
| O‘qituvchi ekrani → hammaga | 8 FPS, JPEG 60, 1280 px, o‘zgargan qism | har o‘quvchiga alohida nusxa: video bo‘lsa ≈ 0.5 MB/s × 16 |

Himoyalar: o‘zgarmagan kadr yuborilmaydi; o‘zgargan qism (32×32 plitkalar chegarasi) alohida JPEG; tarmoq sekin bo‘lsa avtomatik avval sifat, keyin o‘lcham, keyin FPS kamayadi va yaxshilansa tiklanadi; sekin o‘quvchi kadrlarni o‘tkazib yuboradi, qolganlarni sekinlashtirmaydi. Katta sinf (30–40) yoki zaif Wi‑Fi’da **Sozlamalar → Kuzatuv / Ekran sifati** da FPS va kenglikni kamaytiring. Multicast ishlatilmagan.

## Muammolarni hal qilish
| Alomat | Sabab / yechim |
|---|---|
| O‘quvchi "Teacher topilmadi" | boshqa subnet / AP isolation / Teacher yopiq / firewall; Teacher IP’ni qo‘lda kiriting |
| "Classroom code noto‘g‘ri" | kod xato yoki Teacher’da kod yangilangan |
| "Teacher sertifikati ishonchli emas" | Teacher kompyuteri qayta o‘rnatilgan: o‘quvchida Sozlamalar → Xavfsizlik → sertifikatni qayta o‘rnatish |
| Kompyuter "Tasdiqlanmagan" | Teacher’da Ko‘rish → Tasdiqlash |
| Vaqt xatosi (challenge muddati) | kompyuter soatlari 2 daqiqadan ko‘p farq qiladi — vaqtni sinxronlang |
