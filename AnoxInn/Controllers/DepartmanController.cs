using System.Collections.Concurrent;
using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Net;
using System.Net.Mail;
using System.Diagnostics;

namespace AxonInn.Controllers
{
    public class DepartmanController : Controller
    {
        private readonly AxonInnContext _context;

        // ⚡ AKILLI ÖNBELLEK KIRICI: Değişkeni RAM'de korumalı ve thread-safe tutuyoruz.
        public static readonly ConcurrentDictionary<long, string> PersonelFotoVersiyonlari = new();
        public static readonly string AppStartVersion = DateTime.Now.Ticks.ToString();

        // ⚡ PERFORMANS 1: JsonSerializerOptions'ı static readonly yaparak her HTTP isteğinde yeniden yaratılmasını engelledik.
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public DepartmanController(AxonInnContext context)
        {
            _context = context;
        }

        [Route("Departmanlar")]
        [HttpGet]
        public async Task<IActionResult> Departman()
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");
                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);

                if (loginOlanPersonel == null)
                    return RedirectToAction("Login", "Login"); // Null Crash Koruması

                ViewData["GirisYapanPersonel"] = loginOlanPersonel;

                // 🚀 PERFORMANS 2: AsSplitQuery eklendi ve IF blokları içindeki tekrarlanan şartlar ana sorguya (BaseQuery) bağlandı.
                var query = _context.Hotels
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Where(h => h.Departmen.Any(d => d.Id == loginOlanPersonel.DepartmanRef));

                if (loginOlanPersonel.Yetki == 1)
                {
                    query = query.Include(h => h.Departmen)
                                 .ThenInclude(d => d.Personels);
                }
                else if (loginOlanPersonel.Yetki == 2)
                {
                    query = query.Include(h => h.Departmen.Where(d => d.Id == loginOlanPersonel.DepartmanRef))
                                 .ThenInclude(d => d.Personels);
                }
                else if (loginOlanPersonel.Yetki == 3)
                {
                    query = query.Include(h => h.Departmen.Where(d => d.Id == loginOlanPersonel.DepartmanRef))
                                 .ThenInclude(d => d.Personels.Where(p => p.Id == loginOlanPersonel.Id));
                }

                var hotel = await query.FirstOrDefaultAsync();

                if (hotel == null)
                    return RedirectToAction("Login", "Login");

                ViewData["Title"] = "AxonInn - Departmanlar";

                // ⚡ HIZ OPTİMİZASYONU: Veritabanına INSERT atan (Log) işlemini UI okuması bittikten sonra en sona aldık.
                await LogKaydetAsync(loginOlanPersonel, "Departman Sayfasına Giriş Yapıldı", "Sayfa Görüntüleme", null);

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
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson)) return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                yeniPersonel.TelefonNumarasi = FormatTelefon(yeniPersonel.TelefonNumarasi);

                bool kullaniciVarmi = await _context.Personels.AnyAsync(p => p.TelefonNumarasi == yeniPersonel.TelefonNumarasi || p.MailAdresi == yeniPersonel.MailAdresi);

                if (kullaniciVarmi)
                {
                    await LogKaydetAsync(loginOlanPersonel, "Personel Ekleme Hatası", "Mail veya telefon eşleştiği için kayıt yapılamadı.", yeniPersonel);
                    TempData["Mesaj"] = "Mail adresi veya telefon numarası ile eşleşen bir personel sistemde mevcuttur.";
                    TempData["MesajTipi"] = "warning";
                    return RedirectToAction("Departman", "Departman");
                }

                yeniPersonel.AktifMi = 1;
                yeniPersonel.MailOnayliMi = 0;
                yeniPersonel.VerificationToken = Guid.NewGuid().ToString();
                yeniPersonel.Sifre = BCrypt.Net.BCrypt.HashPassword(yeniPersonel.Sifre); ;

                // 🛡️ GÜVENLİK 2: RAM Bombası kalkanı. Sadece resim formatında ve max 5 MB dosyalara izin verilir.
                if (yuklenenFoto != null && yuklenenFoto.Length > 0)
                {
                    if (yuklenenFoto.Length > 5 * 1024 * 1024 || !yuklenenFoto.ContentType.StartsWith("image/"))
                    {
                        TempData["Mesaj"] = "Fotoğraf geçersiz veya 5MB boyutundan büyük olamaz.";
                        TempData["MesajTipi"] = "warning";
                        return RedirectToAction("Departman", "Departman");
                    }
                }

                // ⚡ İŞLEM BÜTÜNLÜĞÜ (TRANSACTION): Personel ve resmi art arda eklerken oluşabilecek çöp (kısmi) kayıtları önler.
                await using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    _context.Personels.Add(yeniPersonel);
                    await _context.SaveChangesAsync(); // Personel'in ID'si oluşsun

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
                        PersonelFotoVersiyonlari[yeniPersonel.Id] = DateTime.Now.Ticks.ToString();
                    }

                    await transaction.CommitAsync(); // Hata çıkmadıysa SQL'e kalıcı yaz
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync(); // Eğer fotoğrafı DB'ye yazarken yer kalmazsa veya patlarsa Personeli de geri al
                    TempData["Mesaj"] = "Veritabanına kullanıcı kayıt edilemedi.";
                    TempData["MesajTipi"] = "error";
                    return RedirectToAction("Departman", "Departman");
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

                return RedirectToAction("Departman", "Departman");
            }
            catch (Exception)
            {
                TempData["Mesaj"] = "Sistemsel bir hata nedeniyle kullanıcı kayıt edilemedi.";
                TempData["MesajTipi"] = "error";
                return RedirectToAction("Departman", "Departman");
            }
        }

        [HttpPost]
        public async Task<IActionResult> PersonelSil(long id)
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson)) return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);

                bool gorevVarMi = await _context.Gorevs.AnyAsync(g => g.PersonelRef == id);

                if (gorevVarMi)
                {
                    await _context.Personels
                                           .Where(p => p.Id == id)
                                           .ExecuteUpdateAsync(s => s.SetProperty(p => p.AktifMi, 2));
                    TempData["Mesaj"] = "Personele kayıtlı görev bulunduğu için silinemez. (Personel Pasife Alındı.)";
                    TempData["MesajTipi"] = "warning";
                    await LogKaydetAsync(loginOlanPersonel, "Personele kayıtlı görev olduğu için silinemez,personel pasife alındı", "Görev atandığı için silinemez.", null);
                    return RedirectToAction("Departman", "Departman");
                }

                // ExecuteDeleteAsync doğrudan SQL'e gittiği için kusursuzdur.
                await _context.PersonelFotografs.Where(f => f.PersonelRef == id).ExecuteDeleteAsync();
                await _context.Personels.Where(p => p.Id == id).ExecuteDeleteAsync();

                // 🧹 RAM TEMİZLİĞİ: Silinen personelin versiyonunu Sözlükten silerek Memory Leak olmasını engelliyoruz.
                PersonelFotoVersiyonlari.TryRemove(id, out _);

                await LogKaydetAsync(loginOlanPersonel, "Personel Silindi", $"Personel ID: {id} başarıyla silindi.", null);

                return RedirectToAction("Departman", "Departman");
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
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson)) return RedirectToAction("Login", "Login");
                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);

                var dbPersonel = await _context.Personels.FindAsync(p.Id);
                if (dbPersonel != null)
                {
                    dbPersonel.Adi = p.Adi;
                    dbPersonel.Soyadi = p.Soyadi;
                    dbPersonel.DepartmanRef = p.DepartmanRef;
                    dbPersonel.TelefonNumarasi = FormatTelefon(p.TelefonNumarasi);
                    dbPersonel.MailAdresi = p.MailAdresi;
                    dbPersonel.MedenHali = p.MedenHali;
                    dbPersonel.Yetki = p.Yetki;
                    dbPersonel.AktifMi = p.AktifMi;

                    if (!string.IsNullOrWhiteSpace(p.Sifre))
                        dbPersonel.Sifre = BCrypt.Net.BCrypt.HashPassword(p.Sifre); 

                    await _context.SaveChangesAsync();

                    if (yuklenenFoto != null && yuklenenFoto.Length > 0 && yuklenenFoto.Length <= 5 * 1024 * 1024 && yuklenenFoto.ContentType.StartsWith("image/"))
                    {
                        using var ms = new MemoryStream((int)yuklenenFoto.Length);
                        await yuklenenFoto.CopyToAsync(ms);
                        var imageBytes = ms.ToArray();

                        // 🚀 BLOB RAM OPTİMİZASYONU:
                        // Eski fotoğrafı (MB'larca büyüklükte olabilir) RAM'e `FirstOrDefaultAsync` ile ÇEKMEDEN!
                        // EF Core 7+ özelliği ile "Doğrudan SQL'de" Update işlemi yapıyoruz. Sunucu RAM'i %0 yoruluyor.
                        var updatedRows = await _context.PersonelFotografs
                            .Where(f => f.PersonelRef == p.Id)
                            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Fotograf, imageBytes));

                        // Eğer sıfır satır güncellendiyse (yani kişinin önceden hiç resmi yoksa) o zaman yeni kayıt Insert ediyoruz.
                        if (updatedRows == 0)
                        {
                            _context.PersonelFotografs.Add(new PersonelFotograf { PersonelRef = p.Id, Fotograf = imageBytes });
                            await _context.SaveChangesAsync();
                        }

                        // Tarayıcının cache'ini kırmak için versiyon anında yenileniyor
                        PersonelFotoVersiyonlari[p.Id] = DateTime.Now.Ticks.ToString();
                    }

                    await LogKaydetAsync(loginOlanPersonel, "Personel Güncellendi", $"Personel ID: {p.Id} başarıyla güncellendi.", dbPersonel);
                }

                return RedirectToAction("Departman", "Departman");
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
                string baseUrl = $"{Request.Scheme}://{Request.Host}";
                string verificationLink = $"{baseUrl}/Login/VerifyEmail?token={token}";

                // ⚡ TEMİZ KOD OPTİMİZASYONU: C# 11 "Raw String Literals" ile (+) koymadan temiz HTML dizilimi eklendi.
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
                    From = new MailAddress("info@axoninn.com.tr", "AxonInn Otomasyon"),
                    Subject = "AxonInn - Hesabınızı Doğrulayın",
                    Body = mailBody,
                    IsBodyHtml = true,
                };

                mailMessage.To.Add(toEmail);

                using var smtpClient = new SmtpClient("mail.axoninn.com.tr")
                {
                    Port = 587,
                    Credentials = new NetworkCredential("info@axoninn.com.tr", "12345+pl"),
                    EnableSsl = false
                };

                await smtpClient.SendMailAsync(mailMessage);
                return true;
            }
            catch (Exception ex)
            {
                // Bilgi sızıntısını önlemek için kullanıcıya yollamayıp sunucu konsoluna basıyoruz
                Console.WriteLine($"Mail gönderme hatası: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> LogKaydetAsync(Personel? personel, string islemTipi, string yeniDeger, Personel? islemGorenPersonel)
        {
            try
            {
                if (personel == null) return false;

                string departmanAdi = "";
                string hotelAdi = "";

                // JSON'dan dönen verinin Navigation değerleri boş olabileceği için veritabanından buluyoruz.
                if (personel.DepartmanRef != 0)
                {
                    var depBilgisi = await _context.Departmen
                        .AsNoTracking()
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => new { DeptAd = d.Adi, OtelAd = d.HotelRefNavigation.Adi })
                        .FirstOrDefaultAsync();

                    if (depBilgisi != null)
                    {
                        departmanAdi = depBilgisi.DeptAd ?? "";
                        hotelAdi = depBilgisi.OtelAd ?? "";
                    }
                }

                var log = new AuditLog
                {
                    IslemTarihi = DateTime.Now,
                    IlgiliTablo = "Personel",
                    KayitRefId = islemGorenPersonel?.Id ?? personel.Id,
                    IslemTipi = islemTipi,
                    EskiDeger = "",
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
        // Yeni bir sınıf (StringBuilder vs.) üretmeden string parçalamayı doğrudan işlemci Stack'inde (Çok Hızlı) çözer.
        private string FormatTelefon(string? telefon)
        {
            if (string.IsNullOrWhiteSpace(telefon)) return string.Empty;

            // Kötü niyetli upuzun metin girişlerine karşı (Stack taşmasını önlemek için) limiti 50 koyuyoruz
            Span<char> digits = stackalloc char[Math.Min(telefon.Length, 50)];
            int index = 0;

            foreach (char c in telefon)
            {
                if (char.IsDigit(c) && index < digits.Length)
                {
                    digits[index++] = c;
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
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);
                var dbPersonel = await _context.Personels.FindAsync(p.Id);

                if (dbPersonel != null)
                {
                    if (dbPersonel.AktifMi==1 && dbPersonel.MailOnayliMi==0 & dbPersonel.VerificationToken!=null)
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


                return RedirectToAction("Departman", "Departman");
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }
    }
}