using AxonInn.Apify;
using Newtonsoft.Json.Linq;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json; // Newtonsoft yerine built-in System.Text.Json tercih edilmeli

namespace AxonInn.Models
{
    [Table("Yorum", Schema = "axoninnc_user")]
    public class Yorum
    {
        [Key]
        public int Id { get; set; }
        public string? MisafirYorumId { get; set; }
        public string? MisafirMemleket { get; set; }
        public string? MisafirYorumBaslik { get; set; }
        public string? MisafirYorum { get; set; }
        public string? MisafirUlkesi { get; set; }
        public string? MisafirKonaklamaTipi { get; set; }
        public string? MisafirKonaklamaTarihi { get; set; }
        public DateTime? MisafirYorumTarihi { get; set; }
        public long HotelRef { get; set; }
        public int? GeminiAnalizYapildiMi { get; set; }
        public string? GeminiAnalizDuyguDurumu { get; set; }
        public int? GeminiAnalizDuyguSkoru { get; set; }
        public string? GeminiAnalizBaskinHis { get; set; }
        public string? GeminiAnalizIlgiliDepartman { get; set; }
        public string? GeminiAnalizAnahtarKelimeler { get; set; }
        public string? GeminiAnalizProfilBeklentisi { get; set; }
        public string? GeminiAnalizKulturelHassasiyet { get; set; }
        public string? GeminiAnalizSezonsalDurum { get; set; }
        public string? GeminiAnalizKisaOzet { get; set; }
        public bool? GeminiAnalizAcilDurumVarMi { get; set; }
        public string? GeminiAnalizTavsiye { get; set; }

        // PERFORMANS: Her metod çağrısında yeni obje yaratmamak için statik (tekil) hale getirildi.
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public Yorum() { }

        public Yorum(JToken jt, long hotelRef)
        {
            MisafirYorumId = (string)jt["id"];
            MisafirMemleket = (string)jt.SelectToken("user.userLocation.name");
            MisafirYorumBaslik = (string)jt["title"];
            MisafirYorum = (string)jt["text"];
            MisafirUlkesi = (string)jt["lang"];
            MisafirKonaklamaTipi = (string)jt["tripType"];
            MisafirKonaklamaTarihi = (string)jt["travelDate"];
            MisafirYorumTarihi = (DateTime?)jt["publishedDate"];
            HotelRef = hotelRef;
        }

        public void GeminiVerileriniIsle(string geminiJsonCevabi)
        {
            try
            {
                var analizSonucu = JsonSerializer.Deserialize<GeminiAnalizSonucu>(geminiJsonCevabi, _jsonOptions);
                if (analizSonucu != null)
                {
                    this.GeminiAnalizDuyguDurumu = analizSonucu.DuyguAnalizi?.Durum;
                    this.GeminiAnalizDuyguSkoru = analizSonucu.DuyguAnalizi?.Skor;
                    this.GeminiAnalizBaskinHis = analizSonucu.DuyguAnalizi?.BaskinHis;
                    this.GeminiAnalizIlgiliDepartman = analizSonucu.DuyguAnalizi?.IlgiliDepartman;
                    this.GeminiAnalizAnahtarKelimeler = analizSonucu.AnahtarKelimeler != null ? string.Join(", ", analizSonucu.AnahtarKelimeler) : null;
                    this.GeminiAnalizProfilBeklentisi = analizSonucu.ProfilBeklentisi;
                    this.GeminiAnalizKulturelHassasiyet = analizSonucu.KulturelHassasiyet;
                    this.GeminiAnalizSezonsalDurum = analizSonucu.SezonsalDurum;
                    this.GeminiAnalizKisaOzet = analizSonucu.KisaOzet;
                    this.GeminiAnalizAcilDurumVarMi = analizSonucu.AcilDurumVarMi;
                    this.GeminiAnalizTavsiye = analizSonucu.MudureTavsiye;
                    this.GeminiAnalizYapildiMi = 1;
                }
            }
            catch (Exception)
            {
                this.GeminiAnalizYapildiMi = 2; // Hata bayrağı
            }
        }
    }
}