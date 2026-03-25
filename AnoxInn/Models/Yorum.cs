using Newtonsoft.Json.Linq;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.RegularExpressions;

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

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public Yorum() { }

       public Yorum(JToken jt, long hotelRef)
{
    // RapidAPI JSON formatına göre veri eşleştirme
    MisafirYorumId = (string)jt.SelectToken("helpfulVote.helpfulVoteAction.objectId");

    // GÜNCELLEME: Memleket ve Ülke bilgisini dinamik ve hatasız çözüyoruz
    MisafirMemleket = null;
    MisafirUlkesi = null;

    JToken hometownToken = jt.SelectToken("userProfile.hometown");
    if (hometownToken != null && hometownToken.Type != JTokenType.Null)
    {
        string tamMemleket = null;

        // SENARYO 1: Veri doğrudan metin olarak geldiyse (Örn: "Moscow, Russia")
        if (hometownToken.Type == JTokenType.String)
        {
            tamMemleket = hometownToken.ToString();
        }
        // SENARYO 2: Veri JSON objesi olarak geldiyse
        else if (hometownToken.Type == JTokenType.Object)
        {
            tamMemleket = (string)hometownToken.SelectToken("fallbackString") ?? 
                          (string)hometownToken.SelectToken("text");
        }

        if (!string.IsNullOrEmpty(tamMemleket))
        {
            MisafirMemleket = tamMemleket.Trim();

            // Memleket verisi "Berlin, Almanya" veya "London, UK" gibi virgüllü geliyorsa, 
            // virgülden sonraki son parçayı "Ülke" olarak alıyoruz.
            if (MisafirMemleket.Contains(","))
            {
                var parcalar = MisafirMemleket.Split(',');
                MisafirUlkesi = parcalar[parcalar.Length - 1].Trim(); // En sondaki elemanı alır (Örn: "Russia")

                // İsteğe bağlı: MisafirMemleket içinde sadece şehri bırakmak istersen aşağıdaki satırı açabilirsin:
                // MisafirMemleket = parcalar[0].Trim(); 
            }
        }
    }

    string htmlBaslik = (string)jt.SelectToken("htmlTitle.htmlString");
    MisafirYorumBaslik = TemizleHTML(htmlBaslik);

    string htmlMetin = (string)jt.SelectToken("htmlText.htmlString");
    MisafirYorum = TemizleHTML(htmlMetin);

    // Konaklama Tipi ve Tarihi "Sep 2025 • Couples" formatında geliyor, ayırıyoruz.
    string bubbleText = (string)jt.SelectToken("bubbleRatingText.text");
    if (!string.IsNullOrEmpty(bubbleText) && bubbleText.Contains("•"))
    {
        var parts = bubbleText.Split('•');
        MisafirKonaklamaTarihi = parts[0].Trim();
        MisafirKonaklamaTipi = parts[1].Trim();
    }
    else
    {
        MisafirKonaklamaTarihi = bubbleText;
    }

    // Yorum Tarihi
    string dateStr = (string)jt.SelectToken("publishedDate.string");
    if (!string.IsNullOrEmpty(dateStr))
    {
        dateStr = dateStr.Replace("Written", "")
                         .Replace("Yazım tarihi:", "")
                         .Replace("Yazıldığı tarih:", "")
                         .Replace("tarihinde yazıldı", "")
                         .Trim();

        if (DateTime.TryParse(dateStr, out DateTime parsedDate))
        {
            MisafirYorumTarihi = parsedDate;
        }
    }

    HotelRef = hotelRef;
}
        public void GeminiVerileriniIsle(string geminiJsonCevabi)
        {
            try
            {
                var analizSonucu = JsonSerializer.Deserialize<GeminiAnalizSonucu>(geminiJsonCevabi, _jsonOptions);
                if (analizSonucu != null)
                {
                    // GÜVENLİK: Veritabanında "String or binary data would be truncated" hatası almamak için kısa olması gereken kolonları kırpıyoruz.
                    // (Veritabanındaki karakter sınırın farklıysa buradaki 50 ve 100 değerlerini kendine göre değiştirebilirsin)
                    this.GeminiAnalizDuyguDurumu = MetniKirp(analizSonucu.DuyguAnalizi?.Durum, 50);
                    this.GeminiAnalizBaskinHis = MetniKirp(analizSonucu.DuyguAnalizi?.BaskinHis, 100);
                    this.GeminiAnalizIlgiliDepartman = MetniKirp(analizSonucu.DuyguAnalizi?.IlgiliDepartman, 100);

                    this.GeminiAnalizDuyguSkoru = analizSonucu.DuyguAnalizi?.Skor;
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

        // HTML etiketlerini (örn: <br />) temizleyen yardımcı metot
        private static string TemizleHTML(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Regex.Replace(text, "<.*?>", " ").Trim();
        }

        // GÜVENLİK: Veritabanı sınırlarını aşan uzun metinleri kırpan yardımcı metot
        private static string? MetniKirp(string? metin, int sinir)
        {
            if (string.IsNullOrEmpty(metin)) return metin;
            return metin.Length > sinir ? metin.Substring(0, sinir) : metin;
        }
    }
}