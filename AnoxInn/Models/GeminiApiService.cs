using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AxonInn.Models
{
    public class GeminiApiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey = "AIzaSyCCUcgnqu5DYl7fGH3Yn5HdChafi1IDuWQ";

        public GeminiApiService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<string> KategorizeEtAsync(string aciklama, string? personelNotu)
        {

            // Modeli gemini-2.5-flash olarak güncelledik ve v1beta endpoint'ine geçtik
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={_apiKey.Trim()}";

            string prompt = $@"
Sen bir otel yönetim sistemi asistanısın. Aşağıdaki görev açıklaması ve personel notunu okuyarak bu görevi şu 10 kategoriden SADECE BİRİNE ata: 
[Teknik Arıza, Kat Hizmetleri, Müşteri Talebi, Güvenlik, Ön Büro, Yiyecek/İçecek, Bilgi İşlem, Satın Alma, Depo, Peyzaj, Havuz, Diğer]

Görev Açıklaması: {aciklama}
Personel Notu: {personelNotu ?? "Not girilmemiş"}

Lütfen SADECE kategori adını yaz. Nokta, tırnak işareti, açıklama veya ek bir cümle KESİNLİKLE kullanma.";

            var payload = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
            var jsonPayload = JsonSerializer.Serialize(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync(url, content);

                if (response.IsSuccessStatusCode)
                {
                    var responseString = await response.Content.ReadAsStringAsync();
                    using JsonDocument doc = JsonDocument.Parse(responseString);
                    var aiCevap = doc.RootElement
                        .GetProperty("candidates")[0]
                        .GetProperty("content")
                        .GetProperty("parts")[0]
                        .GetProperty("text").GetString();

                    return aiCevap?.Trim() ?? "Diğer";
                }
                else
                {
                    // 🚨 İŞTE SİHİRLİ KISIM: GOOGLE'IN BİZE GÖNDERDİĞİ GERÇEK HATAYI OKUYORUZ
                    string hataDetayi = await response.Content.ReadAsStringAsync();
                    try
                    {
                        using JsonDocument errDoc = JsonDocument.Parse(hataDetayi);
                        string gercekHata = errDoc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "Bilinmeyen 404 Hatası";

                        // DB'deki AiKategori NVARCHAR(50) olduğu için hata mesajının ilk 45 harfini alıyoruz (yoksa sistem çöker)
                        //return gercekHata.Length > 45 ? gercekHata.Substring(0, 45) : gercekHata;
                        return "Değerlendirilemeyen";
                    }
                    catch
                    {
                        return $"API Red: {response.StatusCode}";
                    }
                }
            }
            catch (Exception ex)
            {
                //string hata = ex.Message;
                //return hata.Length > 45 ? hata.Substring(0, 45) : hata;
                return "Değerlendirilemeyen";
            }
        }
    }
}