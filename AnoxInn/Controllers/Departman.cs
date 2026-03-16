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

                // DEĞİŞİKLİK: .AsNoTracking() eklendi! 
                // Sadece okuma yapıldığı için EF Core nesneleri takip etmez, RAM ciddi oranda rahatlar.
                var hotel = await _context.Hotels
                    .AsNoTracking()
                    .Include(h => h.Departmen)
                        .ThenInclude(d => d.Personels.Where(p => p.AktifMi == 1))
                    .FirstOrDefaultAsync(h => h.Id == hotelId);

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
                    return RedirectToAction("Departman", "Departman");
                }

                var silinecekPersonel = await _context.Personels.FindAsync(id);
                if (silinecekPersonel != null)
                {
                    // YENİ VE EN HIZLI YÖNTEM: RAM'e hiçbir şey çekmeden, doğrudan SQL bazlı silme.
                    // EF Core 7.0+ destekler.
                    await _context.PersonelFotografs
                        .Where(f => f.PersonelRef == id)
                        .ExecuteDeleteAsync();

                    // Fotoğraf (eğer varsa) silindiğine göre artık ana personeli silebiliriz
                    _context.Personels.Remove(silinecekPersonel);
                    await _context.SaveChangesAsync();

                    await LogKaydet(loginOlanPersonel, "Personel Silindi", $"Personel ID: {id} başarıyla silindi.", null);
                }

                return RedirectToAction("Departman", "Departman");
            }
            catch (Exception ex)
            {
                // Asıl hatayı (Inner Exception) yakalayarak ekranda daha net görünmesini sağlıyoruz.
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
                    // DEĞİŞİKLİK: 2 ayrı SQL sorgusu yerine Navigation Property (HotelRefNavigation) 
                    // kullanarak SQL tarafında JOIN işlemi yapılmasını sağladık. Tek sorgu atılacak.
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
                // Sayfa çökmesin diye hatayı yutuyoruz
                return false;
            }
        }


    }
}