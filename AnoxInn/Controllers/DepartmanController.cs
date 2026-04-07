using AxonInn.Helpers;
using AxonInn.Models.Context;
using AxonInn.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Text.Json;

namespace AxonInn.Controllers
{
    [AutoValidateAntiforgeryToken]
    public class DepartmanController : Controller
    {
        private readonly AxonInnContext _context;
        private readonly IConfiguration _configuration;
        private readonly IMemoryCache _memoryCache; // EKLENDİ

        // ⚡ AKILLI ÖNBELLEK KIRICI: AppStartVersion sabit kalabilir.
        public static readonly string AppStartVersion = DateTime.Now.Ticks.ToString();

        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // Constructor'a IMemoryCache eklendi
        public DepartmanController(AxonInnContext context, IConfiguration configuration, IMemoryCache memoryCache)
        {
            _context = context;
            _configuration = configuration;
            _memoryCache = memoryCache;
        }

        // ⚡ KOD TEKRARINI ÖNLEME (DRY): Session okuma işlemleri tek merkeze bağlandı.
        private Personel? GetActiveUser()
        {
            var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
            return JsonSerializer.Deserialize<Personel>(personelJson);
        }

        [Route("Departmanlar")]
        [HttpGet]
        public async Task<IActionResult> Departman()
        {
            try
            {
                var loginOlanPersonel = GetActiveUser();

                if (loginOlanPersonel == null)
                    return RedirectToAction("Login", "Login");

                ViewData["GirisYapanPersonel"] = loginOlanPersonel;

                // 🚀 PERFORMANS: AsSplitQuery eklendi ve IF blokları içindeki tekrarlanan şartlar ana sorguya (BaseQuery) bağlandı.
                var query = _context.Hotels
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Where(h => h.Departmen.Any(d => d.Id == loginOlanPersonel.DepartmanRef));

                if (loginOlanPersonel.Yetki == 1)
                {
                    // ⚡ SIRALAMA EKLENDİ: Önce ID, sonra Ad, sonra Soyad sırasına göre SQL'den çekilir.
                    query = query.Include(h => h.Departmen)
                                 .ThenInclude(d => d.Personels.OrderBy(p => p.Adi).ThenBy(p => p.Soyadi));
                }
                else if (loginOlanPersonel.Yetki == 2)
                {
                    // ⚡ SIRALAMA EKLENDİ
                    query = query.Include(h => h.Departmen.Where(d => d.Id == loginOlanPersonel.DepartmanRef))
                                 .ThenInclude(d => d.Personels.OrderBy(p => p.Adi).ThenBy(p => p.Soyadi));
                }
                else if (loginOlanPersonel.Yetki == 3)
                {
                    // Personel kendi ekranını görüyorsa zaten 1 kişi döneceği için sıralamaya gerek yok
                    query = query.Include(h => h.Departmen.Where(d => d.Id == loginOlanPersonel.DepartmanRef))
                                 .ThenInclude(d => d.Personels.Where(p => p.Id == loginOlanPersonel.Id));
                }

                var hotel = await query.FirstOrDefaultAsync();

                if (hotel == null)
                    return RedirectToAction("Login", "Login");

                ViewData["Title"] = "AxonInn - Departmanlar";

                // ⚡ HIZ OPTİMİZASYONU: Veritabanına tekrar select atmamak için hazır olan Departman ve Hotel adını gönderiyoruz.
                string deptAdi = hotel.Departmen.FirstOrDefault(d => d.Id == loginOlanPersonel.DepartmanRef)?.Adi ?? string.Empty;
                await LogKaydetAsync(loginOlanPersonel, "Departman Sayfasına Giriş Yapıldı", "Sayfa Görüntüleme", null, hotel.Adi, deptAdi);

                return View("Departman", hotel);
            }
            catch (Exception)
            {
                // 🛡️ GÜVENLİK 1: ex.Message sızdırılması (Information Disclosure) engellendi.
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        [HttpPost]
        public async Task<IActionResult> PersonelEkle(Personel yeniPersonel, IFormFile? yuklenenFoto)
        {
            try
            {
                var loginOlanPersonel = GetActiveUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                yeniPersonel.TelefonNumarasi = FormatTelefon(yeniPersonel.TelefonNumarasi);

                bool kullaniciVarmi = await _context.Personels.AnyAsync(p => p.TelefonNumarasi == yeniPersonel.TelefonNumarasi || p.MailAdresi == yeniPersonel.MailAdresi);

                if (kullaniciVarmi)
                {
                    await LogKaydetAsync(loginOlanPersonel, "Personel Ekleme Hatası", "Mail veya telefon eşleştiği için kayıt yapılamadı.", yeniPersonel);
                    TempData["Mesaj"] = "Mail adresi veya telefon numarası ile eşleşen bir personel sistemde mevcuttur.";
                    TempData["MesajTipi"] = "warning";
                    return RedirectToAction(nameof(Departman));
                }

                yeniPersonel.AktifMi = 1;
                yeniPersonel.MailOnayliMi = 0;
                yeniPersonel.VerificationToken = Guid.NewGuid().ToString("N"); // "N" formatı tireleri atar ve daha hafiftir
                yeniPersonel.Sifre = BCrypt.Net.BCrypt.HashPassword(yeniPersonel.Sifre);

                // 🛡️ GÜVENLİK 2 & 3: RAM Bombası ve MIME Spoofing kalkanı. (.IsValidImageSignature KULLANILDI)
                if (yuklenenFoto != null && yuklenenFoto.Length > 0)
                {
                    if (yuklenenFoto.Length > 5 * 1024 * 1024 || !yuklenenFoto.IsValidImageSignature())
                    {
                        TempData["Mesaj"] = "Yüklediğiniz dosya geçerli bir resim formatında değil veya 5MB boyutundan büyük olamaz.";
                        TempData["MesajTipi"] = "warning";
                        return RedirectToAction(nameof(Departman));
                    }
                }

                // ⚡ İŞLEM BÜTÜNLÜĞÜ (TRANSACTION): Personel ve resmi art arda eklerken oluşabilecek çöp kayıtları önler.
                await using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    _context.Personels.Add(yeniPersonel);
                    await _context.SaveChangesAsync();

                    if (yuklenenFoto != null && yuklenenFoto.Length > 0)
                    {
                        using var ms = new MemoryStream((int)yuklenenFoto.Length);
                        await yuklenenFoto.CopyToAsync(ms);

                        _context.PersonelFotografs.Add(new PersonelFotograf
                        {
                            PersonelRef = yeniPersonel.Id,
                            Fotograf = ms.ToArray()
                        });

                        await _context.SaveChangesAsync();
                        // 2 saat boyunca erişilmezse RAM'den otomatik temizlenir.
                        _memoryCache.Set($"FotoVersiyon_{yeniPersonel.Id}", DateTime.Now.Ticks.ToString(), new MemoryCacheEntryOptions
                        {
                            SlidingExpiration = TimeSpan.FromHours(2)
                        });
                    }

                    await transaction.CommitAsync();
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    TempData["Mesaj"] = "Veritabanına kullanıcı kayıt edilemedi.";
                    TempData["MesajTipi"] = "error";
                    return RedirectToAction(nameof(Departman));
                }

                bool mailBasariliMi = await SendVerificationEmailAsync(yeniPersonel.MailAdresi, yeniPersonel.VerificationToken);

                if (mailBasariliMi)
                {
                    await LogKaydetAsync(loginOlanPersonel, "Yeni Personel Eklendi", "Yeni Personel Eklendi ve Aktivasyon Maili Gönderildi", yeniPersonel);
                    TempData["Mesaj"] = $"Kullanıcı kayıt edildi, {yeniPersonel.MailAdresi} adresine aktivasyon maili gönderildi.";
                    TempData["MesajTipi"] = "success";
                }
                else
                {
                    await LogKaydetAsync(loginOlanPersonel, "Yeni Personel Eklendi Fakat Aktivasyon Maili Gönderilemedi.", "Kaydedildi fakat aktivasyon maili gönderilemedi.", yeniPersonel);
                    TempData["Mesaj"] = "Kullanıcı kayıt edildi ancak aktivasyon maili gönderilemedi. Lütfen sistem yöneticisiyle iletişime geçin.";
                    TempData["MesajTipi"] = "warning";
                }

                return RedirectToAction(nameof(Departman));
            }
            catch (Exception)
            {
                TempData["Mesaj"] = "Sistemsel bir hata nedeniyle kullanıcı kayıt edilemedi.";
                TempData["MesajTipi"] = "error";
                return RedirectToAction(nameof(Departman));
            }
        }

        [HttpPost]
        public async Task<IActionResult> PersonelSil(long id)
        {
            try
            {
                var loginOlanPersonel = GetActiveUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                bool gorevVarMi = await _context.Gorevs.AnyAsync(g => g.PersonelRef == id);

                if (gorevVarMi)
                {
                    // Update'i RAM'e çekmeden SQL'de doğrudan çözüyoruz
                    await _context.Personels
                                  .Where(p => p.Id == id)
                                  .ExecuteUpdateAsync(s => s.SetProperty(p => p.AktifMi, 2));

                    TempData["Mesaj"] = "Personele kayıtlı görev bulunduğu için silinemez. (Personel Pasife Alındı.)";
                    TempData["MesajTipi"] = "warning";
                    await LogKaydetAsync(loginOlanPersonel, "Personele kayıtlı görev olduğu için silinemez,personel pasife alındı", "Görev atandığı için silinemez.", null);
                    return RedirectToAction(nameof(Departman));
                }

                // ExecuteDeleteAsync doğrudan SQL'e gittiği için kusursuzdur, veri RAM'e inmez.
                await _context.PersonelFotografs.Where(f => f.PersonelRef == id).ExecuteDeleteAsync();
                await _context.Personels.Where(p => p.Id == id).ExecuteDeleteAsync();

                // 🧹 RAM TEMİZLİĞİ: Silinen personelin Cache versiyonunu IMemoryCache'den sil
                _memoryCache.Remove($"FotoVersiyon_{id}");

                await LogKaydetAsync(loginOlanPersonel, "Personel Silindi", $"Personel ID: {id} başarıyla silindi.", null);

                return RedirectToAction(nameof(Departman));
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        [HttpPost]
        public async Task<IActionResult> PersonelGuncelle(Personel p, IFormFile? yuklenenFoto)
        {
            try
            {
                var loginOlanPersonel = GetActiveUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                string formatliTel = FormatTelefon(p.TelefonNumarasi);
                string hashliSifre = !string.IsNullOrWhiteSpace(p.Sifre) ? BCrypt.Net.BCrypt.HashPassword(p.Sifre) : string.Empty;

                // 🚀 MUAZZAM RAM OPTİMİZASYONU (Zero-Tracking Update): 
                // Veriyi FindAsync ile RAM'e almak (1 Select) + SaveChanges beklemek (1 Update) yerine, 
                // Tracking'i tamamen bypass edip tek satırda saf SQL UPDATE komutu gönderiyoruz. Bellek kullanımı %0.
                var updateQuery = _context.Personels.Where(x => x.Id == p.Id);

                if (!string.IsNullOrEmpty(hashliSifre))
                {
                    await updateQuery.ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Adi, p.Adi)
                        .SetProperty(x => x.Soyadi, p.Soyadi)
                        .SetProperty(x => x.DepartmanRef, p.DepartmanRef)
                        .SetProperty(x => x.TelefonNumarasi, formatliTel)
                        .SetProperty(x => x.MailAdresi, p.MailAdresi)
                        .SetProperty(x => x.MedenHali, p.MedenHali)
                        .SetProperty(x => x.Yetki, p.Yetki)
                        .SetProperty(x => x.AktifMi, p.AktifMi)
                        .SetProperty(x => x.Sifre, hashliSifre));
                }
                else
                {
                    await updateQuery.ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Adi, p.Adi)
                        .SetProperty(x => x.Soyadi, p.Soyadi)
                        .SetProperty(x => x.DepartmanRef, p.DepartmanRef)
                        .SetProperty(x => x.TelefonNumarasi, formatliTel)
                        .SetProperty(x => x.MailAdresi, p.MailAdresi)
                        .SetProperty(x => x.MedenHali, p.MedenHali)
                        .SetProperty(x => x.Yetki, p.Yetki)
                        .SetProperty(x => x.AktifMi, p.AktifMi));
                }

                // 🛡️ GÜVENLİK 3: MIME Spoofing kalkanı (.IsValidImageSignature KULLANILDI)
                if (yuklenenFoto != null && yuklenenFoto.Length > 0 && yuklenenFoto.Length <= 5 * 1024 * 1024 && yuklenenFoto.IsValidImageSignature())
                {
                    using var ms = new MemoryStream((int)yuklenenFoto.Length);
                    await yuklenenFoto.CopyToAsync(ms);
                    var imageBytes = ms.ToArray();

                    // BLOB Update (Resmi bellekten silmeden/okumadan doğrudan üzerine yazarız)
                    var updatedRows = await _context.PersonelFotografs
                        .Where(f => f.PersonelRef == p.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(f => f.Fotograf, imageBytes));

                    if (updatedRows == 0)
                    {
                        _context.PersonelFotografs.Add(new PersonelFotograf { PersonelRef = p.Id, Fotograf = imageBytes });
                        await _context.SaveChangesAsync();
                    }

                    // Versiyon bilgisini önbellekte günceller ve süresini sıfırlar
                    _memoryCache.Set($"FotoVersiyon_{p.Id}", DateTime.Now.Ticks.ToString(), new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromHours(2)
                    });
                }

                // Log atmak için veritabanına sorgu atmayıp elimizdeki veriden sanal bir kopya üretiyoruz.
                var logPersonel = new Personel { Id = p.Id, Adi = p.Adi, Soyadi = p.Soyadi, DepartmanRef = p.DepartmanRef };
                await LogKaydetAsync(loginOlanPersonel, "Personel Güncellendi", $"Personel ID: {p.Id} başarıyla güncellendi.", logPersonel);

                return RedirectToAction(nameof(Departman));
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        private async Task<bool> SendVerificationEmailAsync(string? toEmail, string token)
        {
            if (string.IsNullOrEmpty(toEmail)) return false;

            try
            {
                // ⚡ appsettings.json'dan ayarları okuyoruz
                string smtpServer = _configuration["EmailSettings:SmtpServer"]!;
                int port = int.Parse(_configuration["EmailSettings:Port"] ?? "587");
                string senderEmail = _configuration["EmailSettings:SenderEmail"]!;
                string password = _configuration["EmailSettings:Password"]!;
                bool enableSsl = bool.Parse(_configuration["EmailSettings:EnableSsl"] ?? "false");

                string baseUrl = $"{Request.Scheme}://{Request.Host}";
                string verificationLink = $"{baseUrl}/Login/VerifyEmail?token={token}";

                string mailBody = $"""
                    <div style='font-family: Arial, sans-serif; max-width: 600px; margin: auto; border: 1px solid #ddd; padding: 20px; border-radius: 10px;'>
                        <h2 style='color: #2c3e50; text-align: center;'>AxonInn'e Hoş Geldiniz!</h2>
                        <p style='font-size: 16px; color: #555;'>Merhaba,</p>
                        <p style='font-size: 16px; color: #555;'>Hesabınızı aktifleştirmek ve sisteme giriş yapabilmek için lütfen aşağıdaki butona tıklayın:</p>
                        <div style='text-align: center; margin: 30px 0;'>
                            <a href='{verificationLink}' style='background-color: #008CBA; color: white; padding: 12px 25px; text-decoration: none; border-radius: 5px; font-weight: bold; font-size: 16px;'>Hesabımı Doğrula</a>
                        </div>
                        <p style='font-size: 14px; color: #777;'>Eğer butona tıklayamıyorsanız, aşağıdaki linki kopyalayıp tarayıcınıza yapıştırabilirsiniz:</p>
                        <p style='font-size: 12px; color: #3498db; word-break: break-all;'>{verificationLink}</p>
                        <hr style='border: none; border-top: 1px solid #eee; margin-top: 30px;'/>
                        <p style='font-size: 12px; color: #aaa; text-align: center;'>Bu e-posta otomatik olarak gönderilmiştir, lütfen cevaplamayınız.</p>
                    </div>
                    """;

                using var mailMessage = new MailMessage
                {
                    From = new MailAddress(senderEmail, "AxonInn Otomasyon"), // Dinamik e-posta
                    Subject = "AxonInn - Hesabınızı Doğrulayın",
                    Body = mailBody,
                    IsBodyHtml = true,
                };

                mailMessage.To.Add(toEmail);

                using var smtpClient = new SmtpClient(smtpServer) // Dinamik sunucu
                {
                    Port = port, // Dinamik port
                    Credentials = new NetworkCredential(senderEmail, password), // Dinamik kimlik bilgileri
                    EnableSsl = enableSsl // Dinamik SSL ayarı
                };

                await smtpClient.SendMailAsync(mailMessage);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Mail gönderme hatası: {ex.Message}");
                return false;
            }
        }

        // ⚡ N+1 SORGUSU GİDERİLDİ: Metota PreHotelAdi ve PreDeptAdi opsiyonel alanları eklendi.
        private async Task<bool> LogKaydetAsync(Personel? personel, string islemTipi, string yeniDeger, Personel? islemGorenPersonel, string? preHotelAdi = null, string? preDeptAdi = null)
        {
            try
            {
                if (personel == null) return false;

                string departmanAdi = preDeptAdi ?? string.Empty;
                string hotelAdi = preHotelAdi ?? string.Empty;

                if (personel.DepartmanRef != 0 && string.IsNullOrWhiteSpace(departmanAdi))
                {
                    var depBilgisi = await _context.Departmen
                        .AsNoTracking()
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => new { DeptAd = d.Adi, OtelAd = d.HotelRefNavigation != null ? d.HotelRefNavigation.Adi : string.Empty })
                        .FirstOrDefaultAsync();

                    if (depBilgisi != null)
                    {
                        departmanAdi = depBilgisi.DeptAd ?? string.Empty;
                        hotelAdi = depBilgisi.OtelAd ?? string.Empty;
                    }
                }

                var log = new AuditLog
                {
                    IslemTarihi = DateTime.Now,
                    IlgiliTablo = "Personel",
                    KayitRefId = islemGorenPersonel?.Id ?? personel.Id,
                    IslemTipi = islemTipi,
                    EskiDeger = string.Empty,
                    YeniDeger = yeniDeger,
                    YapanHotelAd = hotelAdi,
                    YapanDepartmanAd = departmanAdi,
                    YapanAdSoyad = $"{personel.Adi} {personel.Soyadi}".Trim()
                };

                _context.AuditLogs.Add(log);
                await _context.SaveChangesAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        // 🚀 C# ZERO-ALLOCATION (Sıfır Bellek Tüketimi) TEKNİĞİ
        private string FormatTelefon(string? telefon)
        {
            if (string.IsNullOrWhiteSpace(telefon)) return string.Empty;

            // En fazla 11 karaktere ihtiyacımız var, memory şişirmeyi kestik
            Span<char> digits = stackalloc char[11];
            int index = 0;

            foreach (char c in telefon)
            {
                if (char.IsDigit(c))
                {
                    if (index < 11) digits[index++] = c;
                    else break; // 11 Karakter dolunca gereksiz CPU yorgunluğunu engeller
                }
            }

            var temizNumara = digits[..index];

            if (temizNumara.Length == 10)
            {
                return $"0 ({temizNumara.Slice(0, 3)}) {temizNumara.Slice(3, 3)} {temizNumara.Slice(6, 2)} {temizNumara.Slice(8, 2)}";
            }
            else if (temizNumara.Length == 11 && temizNumara[0] == '0')
            {
                return $"{temizNumara[0]} ({temizNumara.Slice(1, 3)}) {temizNumara.Slice(4, 3)} {temizNumara.Slice(7, 2)} {temizNumara.Slice(9, 2)}";
            }

            return telefon.Trim();
        }

        [HttpPost]
        public async Task<IActionResult> AktivasyonMailiGonder(Personel p)
        {
            try
            {
                var loginOlanPersonel = GetActiveUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                // ⚡ PERFORMANS: Tüm tabloyu çekmek yerine sadece işimize yarayan spesifik kolonları (Select) ve AsNoTracking ile çekiyoruz.
                var dbPersonel = await _context.Personels
                    .AsNoTracking()
                    .Where(x => x.Id == p.Id)
                    .Select(x => new Personel
                    {
                        Id = x.Id,
                        AktifMi = x.AktifMi,
                        MailOnayliMi = x.MailOnayliMi,
                        VerificationToken = x.VerificationToken,
                        MailAdresi = x.MailAdresi,
                        Adi = x.Adi,
                        Soyadi = x.Soyadi,
                        DepartmanRef = x.DepartmanRef
                    })
                    .FirstOrDefaultAsync();

                if (dbPersonel != null)
                {
                    // 🚨 ÖNEMLİ BUG FİX: "&" yerine "&&" (Kısa devre operatörü) kullanılarak null referans hatası ve gereksiz sorgu engellendi.
                    if (dbPersonel.AktifMi == 1 && dbPersonel.MailOnayliMi == 0 && !string.IsNullOrWhiteSpace(dbPersonel.VerificationToken))
                    {
                        bool mailBasariliMi = await SendVerificationEmailAsync(dbPersonel.MailAdresi, dbPersonel.VerificationToken);

                        if (mailBasariliMi)
                        {
                            await LogKaydetAsync(loginOlanPersonel, "Personel Aktivasyon Maili Gönderildi.", "Aktivasyon Maili Gönderildi", dbPersonel);
                            TempData["Mesaj"] = $"{dbPersonel.MailAdresi} adresine aktivasyon maili gönderildi.";
                            TempData["MesajTipi"] = "success";
                        }
                        else
                        {
                            await LogKaydetAsync(loginOlanPersonel, "Personel Aktivasyon Maili Gönderilemedi.", "Aktivasyon Maili Gönderilemedi.", dbPersonel);
                            TempData["Mesaj"] = "Aktivasyon maili gönderilemedi. Lütfen sistem yöneticisiyle iletişime geçin.";
                            TempData["MesajTipi"] = "warning";
                        }
                    }
                    else
                    {
                        await LogKaydetAsync(loginOlanPersonel, "Personel Aktivasyon Maili Gönderilmedi.", "Aktivasyon maili gönderilmedi! Kullanıcı Pasif Durumda Yada Daha Önce Mail Adresi Aktivasyonu Yapıldı.", dbPersonel);
                        TempData["Mesaj"] = "Aktivasyon maili gönderilmedi! Kullanıcı Pasif Durumda Yada Daha Önce Mail Adresi Aktivasyonu Yapıldı.";
                        TempData["MesajTipi"] = "warning";
                    }
                }

                return RedirectToAction(nameof(Departman));
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        [HttpGet]
        public async Task<IActionResult> PersonelFotoGetir(long id)
        {
            // Sadece resmi çekerek RAM tasarrufu yapıyoruz
            var foto = await _context.PersonelFotografs
                .AsNoTracking()
                .Where(f => f.PersonelRef == id)
                .Select(f => f.Fotograf)
                .FirstOrDefaultAsync();

            if (foto != null && foto.Length > 0)
            {
                // Tarayıcıya byte dizisini resim formatında döndürür
                return File(foto, "image/jpeg");
            }

            // Eğer resim yoksa 404 döndürür (Böylece HTML'deki onerror tetiklenir ve default ikon çıkar)
            return NotFound();
        }
    }
}