using AxonInn.Helpers;
using AxonInn.Models.Context;
using AxonInn.Models.Entities;
using AxonInn.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonInn.Controllers
{
    [AutoValidateAntiforgeryToken]
    public class DepartmanController : Controller
    {
        private readonly AxonInnContext _context;
        private readonly IConfiguration _configuration;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogService _logService;
        private readonly ICurrentUserService _currentUserService;

        public static readonly string AppStartVersion = DateTime.Now.Ticks.ToString();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            PropertyNameCaseInsensitive = true // ⚡ EKLENDİ: Gelen JSON'daki büyük/küçük harf uyuşmazlığını tolere eder
        };

        public DepartmanController(AxonInnContext context, IConfiguration configuration, IMemoryCache memoryCache, ILogService logService, ICurrentUserService currentUserService)
        {
            _context = context;
            _configuration = configuration;
            _memoryCache = memoryCache;
            _logService = logService;
            _currentUserService = currentUserService;
        }

   

        [Route("Departmanlar")]
        [HttpGet]
        public async Task<IActionResult> Departman()
        {
            try
            {
                    var loginOlanPersonel = _currentUserService.GetUser();

                // ⚡ DÜZELTME 1: Session okunamadıysa önce sil, sonra yönlendir
                if (loginOlanPersonel == null)
        {
            HttpContext.Session.Remove("GirisYapanPersonel"); // Döngüyü kırar
            return RedirectToAction("Login", "Login");
        }

                ViewData["GirisYapanPersonel"] = loginOlanPersonel;

                var query = _context.Hotels
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Where(h => h.Departmen.Any(d => d.Id == loginOlanPersonel.DepartmanRef));

                if (loginOlanPersonel.Yetki == 1)
                {
                    query = query.Include(h => h.Departmen)
                                 .ThenInclude(d => d.Personels.OrderBy(p => p.Adi).ThenBy(p => p.Soyadi));
                }
                else if (loginOlanPersonel.Yetki == 2)
                {
                    query = query.Include(h => h.Departmen.Where(d => d.Id == loginOlanPersonel.DepartmanRef))
                                 .ThenInclude(d => d.Personels.OrderBy(p => p.Adi).ThenBy(p => p.Soyadi));
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

                string deptAdi = hotel.Departmen.FirstOrDefault(d => d.Id == loginOlanPersonel.DepartmanRef)?.Adi ?? string.Empty;

                // 🛠️ DÜZELTME: eskiDeger parametresi string.Empty yapıldı, islemTipi yeniDeger'e kaydırıldı.
                await _logService.LogKaydetAsync(loginOlanPersonel, "Departman Sayfasına Giriş Yapıldı", string.Empty, "Sayfa Görüntüleme", hotel.Adi, deptAdi);

                return View("Departman", hotel);
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        [HttpPost]
        public async Task<IActionResult> PersonelEkle(Personel yeniPersonel, IFormFile? yuklenenFoto)
        {
            try
            {
                var loginOlanPersonel = _currentUserService.GetUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                yeniPersonel.TelefonNumarasi = FormatTelefon(yeniPersonel.TelefonNumarasi);

                bool kullaniciVarmi = await _context.Personels.AnyAsync(p => p.TelefonNumarasi == yeniPersonel.TelefonNumarasi || p.MailAdresi == yeniPersonel.MailAdresi);

                string personelJson = JsonSerializer.Serialize(yeniPersonel, _jsonOptions);

                if (kullaniciVarmi)
                {
                    await _logService.LogKaydetAsync(loginOlanPersonel, "Personel Ekleme Hatası", "Mail veya telefon eşleşti", personelJson, string.Empty, string.Empty);
                    TempData["Mesaj"] = "Mail adresi veya telefon numarası ile eşleşen bir personel sistemde mevcuttur.";
                    TempData["MesajTipi"] = "warning";
                    return RedirectToAction(nameof(Departman));
                }

                yeniPersonel.AktifMi = 1;
                yeniPersonel.MailOnayliMi = 0;
                yeniPersonel.VerificationToken = Guid.NewGuid().ToString("N");
                yeniPersonel.Sifre = BCrypt.Net.BCrypt.HashPassword(yeniPersonel.Sifre);

                if (yuklenenFoto != null && yuklenenFoto.Length > 0)
                {
                    if (yuklenenFoto.Length > 5 * 1024 * 1024 || !yuklenenFoto.IsValidImageSignature())
                    {
                        TempData["Mesaj"] = "Yüklediğiniz dosya geçerli bir resim formatında değil veya 5MB boyutundan büyük olamaz.";
                        TempData["MesajTipi"] = "warning";
                        return RedirectToAction(nameof(Departman));
                    }
                }

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

                personelJson = JsonSerializer.Serialize(yeniPersonel, _jsonOptions);

                if (mailBasariliMi)
                {
                    // 🛠️ DÜZELTME: "Eski Değer Yok" yerine string.Empty standardı uygulandı.
                    await _logService.LogKaydetAsync(loginOlanPersonel, "Yeni Personel Eklendi", string.Empty, personelJson);
                    TempData["Mesaj"] = $"Kullanıcı kayıt edildi, {yeniPersonel.MailAdresi} adresine aktivasyon maili gönderildi.";
                    TempData["MesajTipi"] = "success";
                }
                else
                {
                    // 🛠️ DÜZELTME: "Eski Değer Yok" yerine string.Empty standardı uygulandı.
                    await _logService.LogKaydetAsync(loginOlanPersonel, "Yeni Personel Eklendi (Mail Hatası)", string.Empty, personelJson);
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
                var loginOlanPersonel = _currentUserService.GetUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                bool gorevVarMi = await _context.Gorevs.AnyAsync(g => g.PersonelRef == id);

                if (gorevVarMi)
                {
                    await _context.Personels
                                  .Where(p => p.Id == id)
                                  .ExecuteUpdateAsync(s => s.SetProperty(p => p.AktifMi, 2));

                    TempData["Mesaj"] = "Personele kayıtlı görev bulunduğu için silinemez. (Personel Pasife Alındı.)";
                    TempData["MesajTipi"] = "warning";

                    await _logService.LogKaydetAsync(loginOlanPersonel, "Personel Pasife Alındı", $"Personel ID: {id}", "Görev atandığı için silinemez, pasife alındı");
                    return RedirectToAction(nameof(Departman));
                }

                await _context.PersonelFotografs.Where(f => f.PersonelRef == id).ExecuteDeleteAsync();
                await _context.Personels.Where(p => p.Id == id).ExecuteDeleteAsync();

                _memoryCache.Remove($"FotoVersiyon_{id}");

                await _logService.LogKaydetAsync(loginOlanPersonel, "Personel Silindi", $"Eski ID: {id}", "Kayıt tamamen silindi");

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
                var loginOlanPersonel = _currentUserService.GetUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                string formatliTel = FormatTelefon(p.TelefonNumarasi);
                string hashliSifre = !string.IsNullOrWhiteSpace(p.Sifre) ? BCrypt.Net.BCrypt.HashPassword(p.Sifre) : string.Empty;

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

                if (yuklenenFoto != null && yuklenenFoto.Length > 0 && yuklenenFoto.Length <= 5 * 1024 * 1024 && yuklenenFoto.IsValidImageSignature())
                {
                    using var ms = new MemoryStream((int)yuklenenFoto.Length);
                    await yuklenenFoto.CopyToAsync(ms);
                    var imageBytes = ms.ToArray();

                    var updatedRows = await _context.PersonelFotografs
                        .Where(f => f.PersonelRef == p.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(f => f.Fotograf, imageBytes));

                    if (updatedRows == 0)
                    {
                        _context.PersonelFotografs.Add(new PersonelFotograf { PersonelRef = p.Id, Fotograf = imageBytes });
                        await _context.SaveChangesAsync();
                    }

                    _memoryCache.Set($"FotoVersiyon_{p.Id}", DateTime.Now.Ticks.ToString(), new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromHours(2)
                    });
                }

                var logPersonel = new Personel { Id = p.Id, Adi = p.Adi, Soyadi = p.Soyadi, DepartmanRef = p.DepartmanRef };
                string logPersonelJson = JsonSerializer.Serialize(logPersonel, _jsonOptions);

                // 🛠️ DÜZELTME: "Bilinmiyor" yerine string.Empty standardı uygulandı.
                await _logService.LogKaydetAsync(loginOlanPersonel, "Personel Güncellendi", string.Empty, logPersonelJson);

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
                    From = new MailAddress(senderEmail, "AxonInn Otomasyon"),
                    Subject = "AxonInn - Hesabınızı Doğrulayın",
                    Body = mailBody,
                    IsBodyHtml = true,
                };

                mailMessage.To.Add(toEmail);

                using var smtpClient = new SmtpClient(smtpServer)
                {
                    Port = port,
                    Credentials = new NetworkCredential(senderEmail, password),
                    EnableSsl = enableSsl
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

        private string FormatTelefon(string? telefon)
        {
            if (string.IsNullOrWhiteSpace(telefon)) return string.Empty;

            Span<char> digits = stackalloc char[11];
            int index = 0;

            foreach (char c in telefon)
            {
                if (char.IsDigit(c))
                {
                    if (index < 11) digits[index++] = c;
                    else break;
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
                var loginOlanPersonel = _currentUserService.GetUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

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
                    string dbPersonelJson = JsonSerializer.Serialize(dbPersonel, _jsonOptions);

                    if (dbPersonel.AktifMi == 1 && dbPersonel.MailOnayliMi == 0 && !string.IsNullOrWhiteSpace(dbPersonel.VerificationToken))
                    {
                        bool mailBasariliMi = await SendVerificationEmailAsync(dbPersonel.MailAdresi, dbPersonel.VerificationToken);

                        if (mailBasariliMi)
                        {
                            // 🛠️ DÜZELTME: "Eski Değer Yok" yerine string.Empty standardı uygulandı.
                            await _logService.LogKaydetAsync(loginOlanPersonel, "Personel Aktivasyon Maili Gönderildi.", string.Empty, dbPersonelJson);
                            TempData["Mesaj"] = $"{dbPersonel.MailAdresi} adresine aktivasyon maili gönderildi.";
                            TempData["MesajTipi"] = "success";
                        }
                        else
                        {
                            // 🛠️ DÜZELTME: "Eski Değer Yok" yerine string.Empty standardı uygulandı.
                            await _logService.LogKaydetAsync(loginOlanPersonel, "Personel Aktivasyon Maili Gönderilemedi.", string.Empty, dbPersonelJson);
                            TempData["Mesaj"] = "Aktivasyon maili gönderilemedi. Lütfen sistem yöneticisiyle iletişime geçin.";
                            TempData["MesajTipi"] = "warning";
                        }
                    }
                    else
                    {
                        // 🛠️ DÜZELTME: Eski değere "Kullanıcı Pasif/Onaylı" yazmak yerine, bu bilgiyi doğrudan islemTipi'ne dahil edip eskiDeğer'i boş bıraktık.
                        await _logService.LogKaydetAsync(loginOlanPersonel, "Personel Aktivasyon Maili Gönderilmedi. (Kullanıcı Pasif/Onaylı)", string.Empty, dbPersonelJson);
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
            var foto = await _context.PersonelFotografs
                .AsNoTracking()
                .Where(f => f.PersonelRef == id)
                .Select(f => f.Fotograf)
                .FirstOrDefaultAsync();

            if (foto != null && foto.Length > 0)
            {
                return File(foto, "image/jpeg");
            }

            return NotFound();
        }
    }
}