using AxonInn.Models.Context;
using AxonInn.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AxonInn.Models.Analitik
{
    public class YorumIslem
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

        public async Task<List<Yorum>> VeritabaniYorumListGetirAsync(long hotelID, AxonInnContext context)
        {
            return await context.Yorum.AsNoTracking()
                                      .Where(y => y.HotelRef == hotelID)
                                      .OrderByDescending(y => y.MisafirYorumTarihi)
                                      .ToListAsync();
        }

        public async Task<List<Yorum>> GeminiAnaliziOlanVeritabaniYorumListGetirAsync(long hotelID, AxonInnContext context)
        {
            return await context.Yorum.Where(y => y.HotelRef == hotelID && y.GeminiAnalizYapildiMi == 1)
                                      .OrderByDescending(y => y.MisafirYorumTarihi)
                                      .ToListAsync();
        }

        public async Task<List<Yorum>> GeminiAnaliziOlmayanVeritabaniYorumListGetirAsync(long hotelID, AxonInnContext context)
        {
            return await context.Yorum.Where(y => y.HotelRef == hotelID && y.GeminiAnalizYapildiMi != 1)
                                      .OrderByDescending(y => y.MisafirYorumTarihi)
                                      .ToListAsync();
        }

        // Senkron Bloklama Hatası Giderildi: ASENKRON HALE GETİRİLDİ
        public async Task<List<Yorum>> TripadvisorYorumGetirApifyApiAsync(long hotelID, int getirilecekKayitAdeti, string apiToken)
        {
            string hotelUrl = "https://www.tripadvisor.com.tr/Hotel_Review-g12078952-d23426767-Reviews-Alarcha_Hotels_Resort-Boztepe_Manavgat_Turkish_Mediterranean_Coast.html";
            string ApiUrl = $"https://api.apify.com/v2/acts/maxcopell~tripadvisor-reviews/run-sync-get-dataset-items?token={apiToken}";

            var payload = new { startUrls = new[] { new { url = hotelUrl } }, maxItemsPerQuery = getirilecekKayitAdeti };

            using StringContent requestContent = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, ApiUrl) { Content = requestContent };

            using HttpResponseMessage response = await _httpClient.SendAsync(requestMessage);
            response.EnsureSuccessStatusCode();

            string jsonResponse = await response.Content.ReadAsStringAsync();
            JArray apiResults = JArray.Parse(jsonResponse);

            List<Yorum> yorumList = new List<Yorum>();
            foreach (JToken item in apiResults)
            {
                Yorum cekilenYorum = Yorum.ApifyYorumOlustur(item, hotelID);
                yorumList.Add(cekilenYorum);
            }

            return yorumList;
        }

        // Senkron Bloklama Hatası Giderildi: ASENKRON HALE GETİRİLDİ
        public async Task<List<Yorum>> TripadvisorYorumGetirRapidApiAsync(long hotelID, long tripadvisorHotelID, int getirilecekKayitAdeti, string rapidApiKey)
        {
            List<Yorum> yorumList = new List<Yorum>();
            string apiUrl = "https://travel-advisor.p.rapidapi.com/reviews/v2/list?currency=TRY&units=km&lang=tr_TR";

            int cekilenSayi = 0;
            int sayfaSayaci = 0;
            string guncelUpdateToken = "";
            string durdurulacakYorumId = "0";
            bool hedefYorumaUlasildi = false;

            while (cekilenSayi < getirilecekKayitAdeti && !hedefYorumaUlasildi)
            {
                var payload = new
                {
                    contentType = "hotel",
                    detailId = tripadvisorHotelID,
                    pagee = sayfaSayaci,
                    filters = new object[] { },
                    updateToken = guncelUpdateToken
                };

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    RequestUri = new Uri(apiUrl),
                    Headers =
                    {
                        { "x-rapidapi-key", rapidApiKey },
                        { "x-rapidapi-host", "travel-advisor.p.rapidapi.com" },
                        { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" }
                    },
                    Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json")
                };

                using HttpResponseMessage response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                string jsonResponse = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(jsonResponse)) break;

                JObject apiResult = JObject.Parse(jsonResponse);
                JToken sections = apiResult.SelectToken("data.AppPresentation_queryPoiReviews.sections");
                if (sections == null) break;

                bool yeniYorumBulundu = false;
                guncelUpdateToken = "";

                foreach (JToken section in sections)
                {
                    JToken singleCardContent = section["poiReviewsSingleCardContent"];

                    if (singleCardContent != null && singleCardContent["__typename"]?.ToString() == "AppPresentation_ReviewCard")
                    {
                        string oAnkiYorumId = (string)singleCardContent.SelectToken("helpfulVote.helpfulVoteAction.objectId");

                        if (oAnkiYorumId == durdurulacakYorumId)
                        {
                            hedefYorumaUlasildi = true;
                            break;
                        }

                        if (cekilenSayi >= getirilecekKayitAdeti) break;

                        yorumList.Add(new Yorum(singleCardContent, hotelID));
                        cekilenSayi++;
                        yeniYorumBulundu = true;
                    }

                    if (section["__typename"]?.ToString() == "AppPresentation_SecondaryButton")
                    {
                        guncelUpdateToken = section.SelectToken("link.updateToken")?.ToString() ?? "";
                    }
                }

                if (!yeniYorumBulundu || string.IsNullOrEmpty(guncelUpdateToken) || hedefYorumaUlasildi)
                    break;

                sayfaSayaci += 20;
            }

            return yorumList;
        }

        private string TemizleHTML(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Regex.Replace(text, "<.*?>", string.Empty).Trim();
        }

        public int TripadvisorYorumKaydet(long hotelID, List<Yorum> yorumList, AxonInnContext context)
        {
            if (yorumList == null || !yorumList.Any()) return 0;

            var gelenYorumIDler = yorumList.Select(y => y.MisafirYorumId).Where(id => !string.IsNullOrEmpty(id)).ToList();
            var mevcutYorumIdler = context.Yorum.AsNoTracking()
                                          .Where(y => y.HotelRef == hotelID && gelenYorumIDler.Contains(y.MisafirYorumId))
                                          .Select(y => y.MisafirYorumId)
                                          .ToList();

            var idSet = new HashSet<string>(mevcutYorumIdler);
            var eklenecekYorumlar = new List<Yorum>();

            foreach (var yorum in yorumList)
            {
                //Mükerrer Kayıt Hatası(Duplicate) Düzeltildi: Döngü içerisinde ID Set içerisine eklendi
                if (!string.IsNullOrEmpty(yorum.MisafirYorumId) && !idSet.Contains(yorum.MisafirYorumId))
                {
                    eklenecekYorumlar.Add(yorum);
                    idSet.Add(yorum.MisafirYorumId);
           
                }
            }

            if (eklenecekYorumlar.Any())
            {
                context.Yorum.AddRange(eklenecekYorumlar);
                context.SaveChanges();
            }
          
         

            return eklenecekYorumlar.Count;
        }

        // Senkron Bloklama Hatası Giderildi: ASENKRON HALE GETİRİLDİ
        public async Task<string> GeminiYorumAnaliziYapAsync(List<Yorum> yorumList, string geminiApiKey)
        {
            string geminiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={geminiApiKey}";
            string yorumListJson = System.Text.Json.JsonSerializer.Serialize(yorumList);

            string prompt = $@"Sen AxonInn otel yönetim sistemi için çalışan kıdemli bir turizm ve veri analistisin.                                                                                     
                                Sana aşağıda JSON formatında BİRDEN FAZLA misafir yorumu içeren bir liste veriyorum.                                                                                     
                                Lütfen listedeki HER BİR yorumu; misafirin ülkesini, konaklama tipini ve tarihini de göz önünde bulundurarak aşağıdaki JSON kalıbına göre analiz et ve JSON DİZİSİ döndür.                                                                                    

                                ÖNEMLİ KURALLAR:                                                             
                                1- SADECE JSON formatında bir dizi olarak cevap ver. Herhangi bir markdown (```json vb.) KULLANMA.                                                            
                                2- 'Skor' değeri KESİNLİKLE 1 ile 100 arasında bir TAM SAYI (Integer) olmalıdır.                                          
                                3- 'DuyguAnalizi.Durum' değeri SADECE BİRİ olmalıdır: ""Çok İyi"", ""İyi"", ""Nötr"", ""Kötü"", ""Çok Kötü"".                     
                                4- 'DuyguAnalizi.IlgiliDepartman' değeri SADECE: ""Misafir İlişkileri"", ""Genel Tesis"", ""Personel"", ""Animasyon"", ""Teknik Servis"", ""Kat Hizmetleri"", ""Yiyecek ve İçecek"", ""Satış ve Pazarlama"", ""Ön Büro"", ""İnsan Kaynakları"", ""Güvenlik"".
                                5- 'DuyguAnalizi.BaskinHis' değeri SADECE: ""Neşe"", ""Rahatlık"", ""Memnuniyet"", ""Rahatsızlık"", ""Hayal Kırıklığı"", ""Tekrar Gelme İsteği"", ""Memnuniyetsizlik"", ""Öfke ve Haksızlık Hissi"", ""Şikayet"", ""Harika"", ""Coşku"".

                                İstenen Kalıp:                                                                                    
                                [                                                                                            
                                  {{                                                                                                        
                                      ""YorumId"": ""(Id değerini yaz)"",                                                                                                        
                                      ""DuyguAnalizi"": {{ ""Durum"": """", ""Skor"": 0, ""BaskinHis"": """", ""IlgiliDepartman"": """" }},                                                                                                        
                                      ""AnahtarKelimeler"": [], ""ProfilBeklentisi"": """", ""KulturelHassasiyet"": """", ""SezonsalDurum"": """", ""KisaOzet"": """", ""AcilDurumVarMi"": false, ""MudureTavsiye"": """"                                                                                             
                                  }}                                                                                    
                                ]                                                                                    
                                Yorumlar: {yorumListJson}";

            var requestBody = new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                generationConfig = new { response_mime_type = "application/json" }
            };

            using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, geminiUrl) { Content = content };

            try
            {
                using HttpResponseMessage response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    string responseString = await response.Content.ReadAsStringAsync();
                    using JsonDocument doc = JsonDocument.Parse(responseString);
                    return doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "[]";
                }
                else
                {
                    string errorDetail = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Gemini API Hatası (Kod: {response.StatusCode}): {errorDetail}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Analiz sırasında hata: {ex.Message}");
                throw;
            }
        }

        public async Task<int> YorumlariPartilerHalindeIsleAsync(List<Yorum> dbYorumList, YorumIslem yorumIslem, string apiKey, AxonInnContext _context)
        {
            int partiBuyuklugu = 20;
            int toplamPartiSayisi = (int)Math.Ceiling((double)dbYorumList.Count / partiBuyuklugu);
            int basariylaIslenenToplam = 0;

            for (int i = 0; i < toplamPartiSayisi; i++)
            {
                var suAnkiParti = dbYorumList.Skip(i * partiBuyuklugu).Take(partiBuyuklugu).ToList();
                int buPartideIslenen = await TekPartiyiIsleVeKaydetAsync(suAnkiParti, yorumIslem, apiKey, _context);
                basariylaIslenenToplam += buPartideIslenen;

                if (i < toplamPartiSayisi - 1)
                {
                    //await Task.Delay(40000); // Limitlere takılmamak için bekleme
                }
            }
            return basariylaIslenenToplam;
        }

        public async Task<int> TekPartiyiIsleVeKaydetAsync(List<Yorum> parti, YorumIslem yorumIslem, string apiKey, AxonInnContext _context)
        {
            string topluAnalizCevabi = await yorumIslem.GeminiYorumAnaliziYapAsync(parti, apiKey);
            return await JsonCevabiniYorumlaraUygulaAsync(topluAnalizCevabi, parti, _context);
        }

        public async Task<int> JsonCevabiniYorumlaraUygulaAsync(string jsonCevabi, List<Yorum> parti, AxonInnContext _context)
        {
            int basariliIslem = 0;
            using JsonDocument doc = JsonDocument.Parse(jsonCevabi);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return basariliIslem;

            foreach (JsonElement analizItem in root.EnumerateArray())
            {
                if (analizItem.TryGetProperty("YorumId", out JsonElement idElement))
                {
                    string currentYorumId = idElement.ToString().Trim();
                    var dbYorum = parti.FirstOrDefault(y => y.MisafirYorumId == currentYorumId);

                    if (dbYorum != null)
                    {
                        dbYorum.GeminiVerileriniIsle(analizItem.GetRawText());
                        if (dbYorum.GeminiAnalizYapildiMi == 1)
                        {
                            basariliIslem++;
                        }
                    }
                }
            }

            // Performans: Sadece başarılıysa döngü dışında tek seferde toplu Save edilir.
            if (basariliIslem > 0)
                await _context.SaveChangesAsync();

            return basariliIslem;
        }
    }
}