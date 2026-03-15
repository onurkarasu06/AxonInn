using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace AxonInn.Controllers
{
    public class DepartmanController : Controller
    {
        private readonly AxonInnContext _context;

        public DepartmanController(AxonInnContext context)
        {
            _context = context;
        }

        [Route("Departman/Personel")]
        [HttpGet]
        public async Task<IActionResult> Departman()
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonConvert.DeserializeObject<Personel>(personelJson);
                await LogKaydet(loginOlanPersonel, "Departman Sayfasına Giriş Yapıldı", "Sayfa Görüntüleme", null);

                // Kullanıcının bağlı olduğu departmanın Hotel ID'sini bul
                var hotelId = await _context.Departmen
                    .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                    .Select(d => d.HotelRef)
                    .FirstOrDefaultAsync();

                // Sadece Hotel'i, o hotele bağlı departmanları ve aktif personelleri tek seferde getir
                var hotel = await _context.Hotels
                    .Include(h => h.Departmen)
                        .ThenInclude(d => d.Personels.Where(p => p.AktifMi == 1))
                    .FirstOrDefaultAsync(h => h.Id == hotelId);

                if (hotel == null)
                {
                    return RedirectToAction("Login", "Login");
                }

                ViewData["Title"] = "AxonInn";

                // View dosyamızın adı "Departman.cshtml" olduğu için açıkça belirtiyoruz
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
                var loginOlanPersonel = JsonConvert.DeserializeObject<Personel>(personelJson);

                bool kullaniciVarmi = await _context.Personels.AnyAsync(p => p.TelefonNumarasi == yeniPersonel.TelefonNumarasi || p.MailAdresi == yeniPersonel.MailAdresi);

                if (kullaniciVarmi)
                {
                    await LogKaydet(loginOlanPersonel, "Personel Ekleme Hatası", "Mail adresi veya telefon numarası eşleştiği için kayıt yapılamadı.", yeniPersonel);
                    TempData["Mesaj"] = "Mail adresi veya telefon numarası ile eşleşen bir personel kayıtlı olduğu için kaydet işlemi yapılamaz.";
                    TempData["MesajTipi"] = "warning";
                    return RedirectToAction("Departman", "Departman"); // Hata çözümü: Controller eklendi
                }

                yeniPersonel.AktifMi = 1;
                _context.Personels.Add(yeniPersonel);
                await _context.SaveChangesAsync();

                if (yuklenenFoto != null && yuklenenFoto.Length > 0)
                {
                    using (var ms = new MemoryStream())
                    {
                        await yuklenenFoto.CopyToAsync(ms);
                        var foto = new PersonelFotograf
                        {
                            PersonelRef = yeniPersonel.Id,
                            Fotograf = ms.ToArray()
                        };
                        _context.PersonelFotografs.Add(foto);
                        await _context.SaveChangesAsync();
                    }
                }

                await LogKaydet(loginOlanPersonel, "Yeni Personel Eklendi", "Personel Başarıyla Kaydedildi", yeniPersonel);
                return RedirectToAction("Departman", "Departman"); // Hata çözümü: Controller eklendi
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> PersonelSil(long id)
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson)) return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonConvert.DeserializeObject<Personel>(personelJson);

                bool gorevVarMi = await _context.Gorevs.AnyAsync(g => g.PersonelRef == id);

                if (gorevVarMi)
                {
                    await LogKaydet(loginOlanPersonel, "Personel Silme Hatası", "Personele kayıtlı görev bulunduğu için silinemez.", null);
                    TempData["Mesaj"] = "Personele kayıtlı görev bulunduğu için silinemez.";
                    TempData["MesajTipi"] = "warning";
                    return RedirectToAction("Departman", "Departman"); // Hata çözümü: Controller eklendi
                }

                var silinecekPersonel = await _context.Personels.FindAsync(id);
                if (silinecekPersonel != null)
                {
                    var foto = await _context.PersonelFotografs.FirstOrDefaultAsync(f => f.PersonelRef == id);
                    if (foto != null) _context.PersonelFotografs.Remove(foto);

                    _context.Personels.Remove(silinecekPersonel);
                    await _context.SaveChangesAsync();

                    await LogKaydet(loginOlanPersonel, "Personel Silindi", $"Personel ID: {id} başarıyla silindi.", null);
                }

                return RedirectToAction("Departman", "Departman"); // Hata çözümü: Controller eklendi
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> PersonelGuncelle(Personel p, IFormFile yuklenenFoto)
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson)) return RedirectToAction("Login", "Login");
                var loginOlanPersonel = JsonConvert.DeserializeObject<Personel>(personelJson);

                var dbPersonel = await _context.Personels.FindAsync(p.Id);
                if (dbPersonel != null)
                {
                    dbPersonel.Adi = p.Adi;
                    dbPersonel.Soyadi = p.Soyadi;
                    dbPersonel.DepartmanRef = p.DepartmanRef;
                    dbPersonel.TelefonNumarasi = p.TelefonNumarasi;
                    dbPersonel.MailAdresi = p.MailAdresi;
                    dbPersonel.MedenHali = p.MedenHali;
                    dbPersonel.Yetki = p.Yetki;

                    if (!string.IsNullOrEmpty(p.Sifre))
                    {
                        dbPersonel.Sifre = p.Sifre;
                    }

                    _context.Personels.Update(dbPersonel);

                    if (yuklenenFoto != null && yuklenenFoto.Length > 0)
                    {
                        var mevcutFoto = await _context.PersonelFotografs.FirstOrDefaultAsync(f => f.PersonelRef == p.Id);

                        using (var ms = new MemoryStream())
                        {
                            await yuklenenFoto.CopyToAsync(ms);
                            if (mevcutFoto != null)
                            {
                                mevcutFoto.Fotograf = ms.ToArray();
                                _context.PersonelFotografs.Update(mevcutFoto);
                            }
                            else
                            {
                                _context.PersonelFotografs.Add(new PersonelFotograf { PersonelRef = p.Id, Fotograf = ms.ToArray() });
                            }
                        }
                    }

                    await _context.SaveChangesAsync();
                    await LogKaydet(loginOlanPersonel, "Personel Güncellendi", $"Personel ID: {p.Id} başarıyla güncellendi.", dbPersonel);
                }

                return RedirectToAction("Departman", "Departman"); // Hata çözümü: Controller eklendi
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
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
                    var hotelId = await _context.Departmen.Where(d => d.Id == personel.DepartmanRef).Select(d => d.HotelRef).FirstOrDefaultAsync();
                    if (hotelId != 0) hotelAdi = await _context.Hotels.Where(h => h.Id == hotelId).Select(h => h.Adi).FirstOrDefaultAsync() ?? "";
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
    }
}