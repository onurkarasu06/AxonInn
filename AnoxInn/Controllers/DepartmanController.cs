using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json; // YENİ NESİL, HIZLI JSON KÜTÜPHANESİ
using System.Net;
using System.Net.Mail;
using System.IO;

namespace AxonInn.Controllers
{
    public class DepartmanController : Controller
    {
        private readonly AxonInnContext _context;

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

                // System.Text.Json ile Deserialization
                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);
                await LogKaydet(loginOlanPersonel, "Departman Sayfasına Giriş Yapıldı", "Sayfa Görüntüleme", null);

                var hotel = await _context.Hotels
                    .AsNoTracking()
                    .Include(h => h.Departmen)
                        .ThenInclude(d => d.Personels.Where(p => p.AktifMi == 1))
                    .FirstOrDefaultAsync(h => h.Departmen.Any(d => d.Id == loginOlanPersonel.DepartmanRef));

                if (hotel == null)
                {
                    return RedirectToAction("Login", "Login");
                }

                ViewData["Title"] = "AxonInn";
                return View("Departman", hotel);
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> PersonelEkle(Personel yeniPersonel, IFormFile yuklenenFoto)
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);

                yeniPersonel.TelefonNumarasi = FormatTelefon(yeniPersonel.TelefonNumarasi);

                bool kullaniciVarmi = await _context.Personels.AnyAsync(p => p.TelefonNumarasi == yeniPersonel.TelefonNumarasi || p.MailAdresi == yeniPersonel.MailAdresi);

                if (kullaniciVarmi)
                {
                    await LogKaydet(loginOlanPersonel, "Personel Ekleme Hatası", "Mail adresi veya telefon numarası eşleştiği için kayıt yapılamadı.", yeniPersonel);
                    TempData["Mesaj"] = "Mail adresi veya telefon numarası ile eşleşen bir personel kayıtlı olduğu için kaydet işlemi yapılamaz.";
                    TempData["MesajTipi"] = "warning";
                    return RedirectToAction("Departman", "Departman");
                }

                yeniPersonel.AktifMi = 1;
                yeniPersonel.MailOnayliMi = 0;
                yeniPersonel.VerificationToken = Guid.NewGuid().ToString();

                _context.Personels.Add(yeniPersonel);

                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (Exception)
                {
                    TempData["Mesaj"] = "Veritabanına kullanıcı kayıt edilemedi.";
                    TempData["MesajTipi"] = "error";
                    return RedirectToAction("Departman", "Departman");
                }

                if (yuklenenFoto != null && yuklenenFoto.Length > 0)
                {
                    using var ms = new MemoryStream();
                    await yuklenenFoto.CopyToAsync(ms);

                    var foto = new PersonelFotograf
                    {
                        PersonelRef = yeniPersonel.Id,
                        Fotograf = ms.ToArray()
                    };
                    _context.PersonelFotografs.Add(foto);
                    await _context.SaveChangesAsync();
                }

                bool mailBasariliMi = await SendVerificationEmailAsync(yeniPersonel.MailAdresi, yeniPersonel.VerificationToken);

                if (mailBasariliMi)
                {
                    await LogKaydet(loginOlanPersonel, "Yeni Personel Eklendi", "Personel Başarıyla Kaydedildi ve Doğrulama Maili Gönderildi", yeniPersonel);
                    TempData["Mesaj"] = "Kullanıcı kayıt edildi, kendi mailinden aktive etmesi gerekmektedir.";
                    TempData["MesajTipi"] = "success";
                }
                else
                {
                    await LogKaydet(loginOlanPersonel, "Yeni Personel Eklendi (Mail Hatası)", "Personel kaydedildi fakat doğrulama maili gönderilemedi.", yeniPersonel);
                    TempData["Mesaj"] = "Kullanıcı kayıt edildi ancak doğrulama maili gönderilemedi. Lütfen sistem yöneticisiyle iletişime geçin.";
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
                    await LogKaydet(loginOlanPersonel, "Personel Silme Hatası", "Personele kayıtlı görev bulunduğu için silinemez.", null);
                    TempData["Mesaj"] = "Personele kayıtlı görev bulunduğu için silinemez.";
                    TempData["MesajTipi"] = "warning";
                    return RedirectToAction("Departman", "Departman");
                }

                await _context.PersonelFotografs.Where(f => f.PersonelRef == id).ExecuteDeleteAsync();
                await _context.Personels.Where(p => p.Id == id).ExecuteDeleteAsync();

                await LogKaydet(loginOlanPersonel, "Personel Silindi", $"Personel ID: {id} başarıyla silindi.", null);

                return RedirectToAction("Departman", "Departman");
            }
            catch (Exception ex)
            {
                string gercekHata = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = gercekHata });
            }
        }

        [HttpPost]
        public async Task<IActionResult> PersonelGuncelle(Personel p, IFormFile yuklenenFoto)
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

                    if (!string.IsNullOrEmpty(p.Sifre))
                    {
                        dbPersonel.Sifre = p.Sifre;
                    }

                    if (yuklenenFoto != null && yuklenenFoto.Length > 0)
                    {
                        var mevcutFoto = await _context.PersonelFotografs.FirstOrDefaultAsync(f => f.PersonelRef == p.Id);

                        using var ms = new MemoryStream();
                        await yuklenenFoto.CopyToAsync(ms);
                        if (mevcutFoto != null)
                        {
                            mevcutFoto.Fotograf = ms.ToArray();
                        }
                        else
                        {
                            _context.PersonelFotografs.Add(new PersonelFotograf { PersonelRef = p.Id, Fotograf = ms.ToArray() });
                        }
                    }

                    await _context.SaveChangesAsync();
                    await LogKaydet(loginOlanPersonel, "Personel Güncellendi", $"Personel ID: {p.Id} başarıyla güncellendi.", dbPersonel);
                }

                return RedirectToAction("Departman", "Departman");
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        private async Task<bool> SendVerificationEmailAsync(string toEmail, string token)
        {
            try
            {
                string baseUrl = $"{Request.Scheme}://{Request.Host}";
                string verificationLink = $"{baseUrl}/Login/VerifyEmail?token={token}";

                var mailMessage = new MailMessage
                {
                    From = new MailAddress("info@axoninn.com.tr", "AxonInn Otomasyon"),
                    Subject = "AxonInn - Hesabınızı Doğrulayın",
                    Body = $@"                 <div style='font-family: Arial, sans-serif; max-width: 600px; margin: auto; border: 1px solid #ddd; padding: 20px; border-radius: 10px;'>                     <h2 style='color: #2c3e50; text-align: center;'>AxonInn'e Hoş Geldiniz!</h2>                     <p style='font-size: 16px; color: #555;'>Merhaba,</p>                     <p style='font-size: 16px; color: #555;'>Hesabınızı aktifleştirmek ve sisteme giriş yapabilmek için lütfen aşağıdaki butona tıklayın:</p>                     <div style='text-align: center; margin: 30px 0;'>                         <a href='{verificationLink}' style='background-color: #008CBA; color: white; padding: 12px 25px; text-decoration: none; border-radius: 5px; font-weight: bold; font-size: 16px;'>Hesabımı Doğrula</a>                     </div>                     <p style='font-size: 14px; color: #777;'>Eğer butona tıklayamıyorsanız, aşağıdaki linki kopyalayıp tarayıcınıza yapıştırabilirsiniz:</p>                     <p style='font-size: 12px; color: #3498db; word-break: break-all;'>{verificationLink}</p>                     <hr style='border: none; border-top: 1px solid #eee; margin-top: 30px;'/>                     <p style='font-size: 12px; color: #aaa; text-align: center;'>Bu e-posta otomatik olarak gönderilmiştir, lütfen cevaplamayınız.</p>                 </div>",
                    IsBodyHtml = true,
                };

                mailMessage.To.Add(toEmail);

                using (var smtpClient = new SmtpClient("mail.axoninn.com.tr"))
                {
                    smtpClient.Port = 587;
                    smtpClient.Credentials = new NetworkCredential("info@axoninn.com.tr", "12345+pl");
                    smtpClient.EnableSsl = false;

                    await smtpClient.SendMailAsync(mailMessage);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Mail gönderme hatası: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> LogKaydet(Personel? personel, string islemTipi, string yeniDeger, Personel? islemGorenPersonel)
        {
            try
            {
                string departmanAdi = personel?.DepartmanRefNavigation?.Adi ?? "";
                string hotelAdi = "";

                if (personel != null && personel.DepartmanRef != 0)
                {
                    hotelAdi = await _context.Departmen
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => d.HotelRefNavigation.Adi)
                        .FirstOrDefaultAsync() ?? "";
                }

                var log = new AuditLog
                {
                    IslemTarihi = DateTime.Now,
                    IlgiliTablo = "Personel",
                    KayitRefId = islemGorenPersonel?.Id ?? personel?.Id ?? 0,
                    IslemTipi = islemTipi,
                    EskiDeger = "",
                    YeniDeger = yeniDeger,
                    YapanHotelAd = hotelAdi,
                    YapanDepartmanAd = departmanAdi,
                    YapanAdSoyad = personel != null ? $"{personel.Adi} {personel.Soyadi}" : "Bilinmeyen"
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

        private string FormatTelefon(string telefon)
        {
            if (string.IsNullOrWhiteSpace(telefon)) return telefon;

            var digits = new string(telefon.Where(char.IsDigit).ToArray());

            if (digits.Length == 10)
            {
                return $"0 ({digits.Substring(0, 3)}) {digits.Substring(3, 3)} {digits.Substring(6, 2)} {digits.Substring(8, 2)}";
            }
            else if (digits.Length == 11 && digits.StartsWith("0"))
            {
                return $"{digits.Substring(0, 1)} ({digits.Substring(1, 3)}) {digits.Substring(4, 3)} {digits.Substring(7, 2)} {digits.Substring(9, 2)}";
            }

            return telefon.Trim();
        }
    }
}