using AxonInn.Models.Entities;

namespace AxonInn.Models.Analitik
{
    public class YorumDashboardGrafikServisi
    {
        public DuyguPastaGrafigiVerisi HesaplaDuyguPastaGrafigi(List<Yorum> yorumlar)
        {
            var veri = new DuyguPastaGrafigiVerisi();

            if (yorumlar == null || !yorumlar.Any())
                return veri;

            foreach (var y in yorumlar)
            {
                // Null değerleri yakala ve Türkçe kültürüyle (TR-tr) güvenli bir şekilde büyük harfe çevir
                string durum = y.GeminiAnalizDuyguDurumu?.Trim().ToUpper(new System.Globalization.CultureInfo("tr-TR")) ?? "";

                // Artık sadece BÜYÜK HARFLİ halleriyle karşılaştırıyoruz
                if (durum == "ÇOK İYİ" || durum == "ÇOK IYI")
                {
                    veri.CokIyiSayisi++;
                }
                else if (durum == "İYİ" || durum == "IYI")
                {
                    veri.IyiSayisi++;
                }
                else if (durum == "KÖTÜ" || durum == "KOTU")
                {
                    veri.KotuSayisi++;
                }
                else if (durum == "ÇOK KÖTÜ" || durum == "ÇOK KOTU")
                {
                    veri.CokKotuSayisi++;
                }
                else if (durum == "NÖTR" || durum == "NOTR")
                {
                    veri.NotrSayisi++;
                }
            }

            return veri;
        }

        public DepartmanBasariGrafigiVerisi HesaplaDepartmanBarGrafigi(List<Yorum> yorumlar)
        {
            if (yorumlar == null || !yorumlar.Any()) return new DepartmanBasariGrafigiVerisi();

            // 1. Sadece grafikte görünmesini istediğimiz kesin departman listesi
            var gecerliDepartmanlar = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                                    {
                                                        "Animasyon",
                                                        "Genel Tesis",
                                                        "Güvenlik",
                                                        "Kat Hizmetleri",
                                                        "Misafir İlişkileri",
                                                        "Ön Büro",
                                                        "Personel",
                                                        "Resepsiyon",
                                                        "Yiyecek ve İçecek"
                                                    };

            var deptIstatistikleri = new Dictionary<string, (double ToplamSkor, int Sayi)>();

            foreach (var y in yorumlar)
            {
                // Eğer boş veya tanımsız gelirse "Genel Tesis" kabul et
                string rawDept = string.IsNullOrWhiteSpace(y.GeminiAnalizIlgiliDepartman) ? "Genel Tesis" : y.GeminiAnalizIlgiliDepartman.Trim();
                if (rawDept.Equals("belirtilmemiş", StringComparison.OrdinalIgnoreCase) || rawDept.Equals("Genel", StringComparison.OrdinalIgnoreCase))
                {
                    rawDept = "Genel Tesis";
                }

                // Eski analiz edilmiş verilerde virgül kalmış olma ihtimaline karşı parçalamayı tutuyoruz
                var departmanDizisi = rawDept.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var d in departmanDizisi)
                {
                    string temizDept = d.Trim();

                    // 2. Kontrol: Gelen departman listemizde YOKSA veri kaybı olmaması için "Genel Tesis" sayıyoruz
                    if (!gecerliDepartmanlar.Contains(temizDept))
                    {
                        temizDept = "Genel Tesis";
                    }
                    else
                    {
                        // Listemizde varsa, bizim listemizdeki orijinal ve şık (Büyük/Küçük harfli) halini alıyoruz
                        temizDept = gecerliDepartmanlar.First(x => x.Equals(temizDept, StringComparison.OrdinalIgnoreCase));
                    }

                    if (!deptIstatistikleri.ContainsKey(temizDept))
                        deptIstatistikleri[temizDept] = (0, 0);

                    var mevcut = deptIstatistikleri[temizDept];

                    // Skor boş gelirse sıfır kabul et (1-100 arası bir değer)
                    double skor = y.GeminiAnalizDuyguSkoru ?? 0;

                    deptIstatistikleri[temizDept] = (mevcut.ToplamSkor + skor, mevcut.Sayi + 1);
                }
            }

            // 3. Ortalama hesaplama ve sıralama işlemi (* 10 çarpımından kurtarılmış hali)
            var siraliListe = deptIstatistikleri.Select(kvp => new
            {
                Isim = kvp.Key,
                Ortalama = Math.Round(kvp.Value.ToplamSkor / kvp.Value.Sayi, 1)
            }).OrderByDescending(x => x.Ortalama).ToList();

            return new DepartmanBasariGrafigiVerisi
            {
                Departmanlar = siraliListe.Select(x => x.Isim).ToList(),
                BasariOranlari = siraliListe.Select(x => x.Ortalama).ToList()
            };
        }

        public HisPolarGrafigiVerisi HesaplaHisPolarGrafigi(List<Yorum> yorumlar)
        {
            if (yorumlar == null || !yorumlar.Any()) return new HisPolarGrafigiVerisi();

            // 1. Sadece grafikte görünmesini istediğimiz kesin his listesi (Beyaz Liste)
            var gecerliHisler = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Neşe",
        "Rahatlık",
        "Memnuniyet",
        "Rahatsızlık",
        "Hayal Kırıklığı",
        "Tekrar Gelme İsteği",
        "Memnuniyetsizlik",
        "Öfke ve Haksızlık Hissi",
        "Şikayet",
        "Harika",
        "Coşku"
    };

            var gecerliYorumlar = yorumlar.Where(y => !string.IsNullOrWhiteSpace(y.GeminiAnalizBaskinHis)).ToList();

            var hisFrekanslari = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int toplamHisSayisi = 0;

            foreach (var y in gecerliYorumlar)
            {
                string his = y.GeminiAnalizBaskinHis.Trim();

                // 2. Kontrol: Veritabanındaki his, bizim belirlediğimiz 11 hissiyatın içinde var mı?
                if (gecerliHisler.Contains(his))
                {
                    string temizHis = gecerliHisler.First(x => x.Equals(his, StringComparison.OrdinalIgnoreCase));

                    if (!hisFrekanslari.ContainsKey(temizHis))
                    {
                        hisFrekanslari[temizHis] = 0;
                    }

                    hisFrekanslari[temizHis]++;
                    toplamHisSayisi++;
                }
            }

            if (toplamHisSayisi == 0) return new HisPolarGrafigiVerisi();

            // 3. İlk 5 kısıtlaması kaldırıldı, geçerli olan TÜM hisler frekansa göre sıralanıp alınıyor
            var tumHisData = hisFrekanslari
                .Select(kvp => new { His = kvp.Key, Frekans = kvp.Value })
                .OrderByDescending(x => x.Frekans)
                .ToList();

            return new HisPolarGrafigiVerisi
            {
                Etiketler = tumHisData.Select(x => x.His).ToList(),
                Oranlar = tumHisData.Select(x => Math.Round(((double)x.Frekans / toplamHisSayisi) * 100, 1)).ToList()
            };
        }

        public KelimeBarGrafigiVerisi HesaplaKelimeBarGrafigi(List<Yorum> yorumlar)
        {
            if (yorumlar == null || !yorumlar.Any()) return new KelimeBarGrafigiVerisi();

            int toplamYorumSayisi = yorumlar.Count;

            var top10Kelimeler = yorumlar.Where(y => !string.IsNullOrWhiteSpace(y.GeminiAnalizAnahtarKelimeler))
                .SelectMany(y => y.GeminiAnalizAnahtarKelimeler.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(k => k.Trim().ToLower()).Where(k => k.Length > 2)
                .GroupBy(k => char.ToUpper(k[0]) + k.Substring(1))
                .Select(g => new { Kelime = g.Key, Frekans = g.Count() })
                .OrderByDescending(x => x.Frekans).Take(10).ToList();

            return new KelimeBarGrafigiVerisi
            {
                Kelimeler = top10Kelimeler.Select(x => x.Kelime).ToList(),
                Oranlar = top10Kelimeler.Select(x => Math.Round(((double)x.Frekans / toplamYorumSayisi) * 100, 1)).ToList()
            };
        }

        public UlkeMemnuniyetGrafigiVerisi HesaplaUlkeGrafigi(List<Yorum> yorumlar)
        {
            var veri = new UlkeMemnuniyetGrafigiVerisi();
            if (yorumlar == null || !yorumlar.Any()) return veri;

            // Ülke isimlerini standartlaştıran yerel bir yardımcı fonksiyon
            string UlkeAdiniStandartlastir(string rawUlke)
            {
                if (string.IsNullOrWhiteSpace(rawUlke)) return "Bilinmiyor";

                string kucukHarf = rawUlke.Trim().ToLowerInvariant();

                switch (kucukHarf)
                {
                    case "tr":
                    case "tr-tr":
                    case "turkey":
                    case "türkiye":
                    case "turkiye":
                        return "Türkiye";
                    case "de":
                    case "de-de":
                    case "germany":
                    case "almanya":
                        return "Almanya";
                    case "ru":
                    case "ru-ru":
                    case "russia":
                    case "rusya":
                    case "russian federation":
                        return "Rusya";
                    case "en":
                    case "en-gb":
                    case "uk":
                    case "gb":
                    case "england":
                    case "ingiltere":
                    case "united kingdom":
                        return "İngiltere";
                    case "us":
                    case "en-us":
                    case "usa":
                    case "amerika":
                    case "abd":
                    case "united states":
                        return "Amerika Birleşik Devletleri";
                    case "nl":
                    case "nl-nl":
                    case "netherlands":
                    case "hollanda":
                        return "Hollanda";
                    case "fr":
                    case "fr-fr":
                    case "france":
                    case "fransa":
                        return "Fransa";
                    default:
                        if (kucukHarf.Length > 1) return char.ToUpper(kucukHarf[0]) + kucukHarf.Substring(1);
                        return rawUlke.ToUpper();
                }
            }

            var grupluYorumlar = yorumlar
                .Where(y => !string.IsNullOrWhiteSpace(y.MisafirUlkesi) &&
                            !y.MisafirUlkesi.Trim().Equals("bilinmiyor", StringComparison.OrdinalIgnoreCase) &&
                            !y.MisafirUlkesi.Trim().Equals("null", StringComparison.OrdinalIgnoreCase))
                .GroupBy(y => UlkeAdiniStandartlastir(y.MisafirUlkesi))
                .Where(g => g.Key != "Bilinmiyor")
                // Grafikte en büyük pazarlar (en çok yorum gelenler) en solda görünsün diye sıralıyoruz
                .OrderByDescending(g => g.Count())
                .ToList();

            foreach (var grup in grupluYorumlar)
            {
                veri.Ulkeler.Add(grup.Key);
                int toplam = grup.Count();

                int pozitif = 0;
                int negatif = 0;
                int notr = 0;

                foreach (var y in grup)
                {
                    string durum = y.GeminiAnalizDuyguDurumu?.Trim() ?? "";

                    if (durum == "Çok Iyi" || durum == "Çok İyi" || durum == "çok iyi" ||
                        durum == "Iyi" || durum == "İyi" || durum == "iyi")
                    {
                        pozitif++;
                    }
                    else if (durum == "Kötü" || durum == "kötü" || durum == "Kotu" ||
                             durum == "Çok Kötü" || durum == "çok kötü" || durum == "Çok Kotu")
                    {
                        negatif++;
                    }
                    else
                    {
                        notr++; // Geriye kalanlar (Nötr, boş vs.)
                    }
                }

                if (toplam > 0)
                {
                    veri.PozitifOranlari.Add(Math.Round(((double)pozitif / toplam) * 100, 1));
                    veri.NotrOranlari.Add(Math.Round(((double)notr / toplam) * 100, 1));
                    veri.NegatifOranlari.Add(Math.Round(((double)negatif / toplam) * 100, 1));
                }
                else
                {
                    veri.PozitifOranlari.Add(0); veri.NotrOranlari.Add(0); veri.NegatifOranlari.Add(0);
                }
            }

            return veri;
        }

        public KonaklamaTipiGrafigiVerisi HesaplaKonaklamaTipiGrafigi(List<Yorum> yorumlar)
        {
            var veri = new KonaklamaTipiGrafigiVerisi();
            if (yorumlar == null || !yorumlar.Any()) return veri;

            // 1. Sadece grafikte her zaman görünmesini istediğimiz 4 uluslararası konaklama tipi
            var gecerliTipler = new List<string> { "COUPLES", "FAMILY", "FRIENDS", "SOLO" };

            // 2. Her tip için başlangıçta 0'dan oluşan boş birer kova hazırlıyoruz
            var tipIstatistikleri = new Dictionary<string, (int Pozitif, int Negatif, int Notr)>(StringComparer.OrdinalIgnoreCase);
            foreach (var tip in gecerliTipler)
            {
                tipIstatistikleri[tip] = (0, 0, 0);
            }

            var gecerliYorumlar = yorumlar.Where(y => !string.IsNullOrWhiteSpace(y.MisafirKonaklamaTipi)).ToList();

            // 3. Yorumları tarayıp sadece listemizde olanları (COUPLES, FAMILY vb.) kendi kovasına atıyoruz
            foreach (var y in gecerliYorumlar)
            {
                string tip = y.MisafirKonaklamaTipi.Trim();

                // Eğer veritabanındaki tip bizim 4'lü listemizde varsa işleme al
                if (tipIstatistikleri.ContainsKey(tip))
                {
                    // İsimdeki büyük/küçük harf farkını düzeltmek için listemizdeki orijinal (Büyük harfli) halini alıyoruz
                    string temizTip = gecerliTipler.First(x => x.Equals(tip, StringComparison.OrdinalIgnoreCase));

                    string durum = y.GeminiAnalizDuyguDurumu?.Trim() ?? "";
                    var mevcut = tipIstatistikleri[temizTip];

                    // 5'li duygu ölçeğimizi 3'lü bar (Yeşil/Kırmızı/Sarı) için grupluyoruz
                    if (durum == "Çok Iyi" || durum == "Çok İyi" || durum == "çok iyi" ||
                        durum == "Iyi" || durum == "İyi" || durum == "iyi")
                    {
                        tipIstatistikleri[temizTip] = (mevcut.Pozitif + 1, mevcut.Negatif, mevcut.Notr);
                    }
                    else if (durum == "Kötü" || durum == "kötü" || durum == "Kotu" ||
                             durum == "Çok Kötü" || durum == "çok kötü" || durum == "Çok Kotu")
                    {
                        tipIstatistikleri[temizTip] = (mevcut.Pozitif, mevcut.Negatif + 1, mevcut.Notr);
                    }
                    else
                    {
                        tipIstatistikleri[temizTip] = (mevcut.Pozitif, mevcut.Negatif, mevcut.Notr + 1); // Kalanlar Nötr
                    }
                }
            }

            // 4. Sonuçları oranlara (% yüzdelik) çevirip grafiğe gönderiyoruz
            foreach (var tip in gecerliTipler)
            {
                veri.Tipler.Add(tip);
                var istatistik = tipIstatistikleri[tip];
                int toplam = istatistik.Pozitif + istatistik.Negatif + istatistik.Notr;

                if (toplam > 0)
                {
                    veri.PozitifOranlari.Add(Math.Round(((double)istatistik.Pozitif / toplam) * 100, 1));
                    veri.NotrOranlari.Add(Math.Round(((double)istatistik.Notr / toplam) * 100, 1));
                    veri.NegatifOranlari.Add(Math.Round(((double)istatistik.Negatif / toplam) * 100, 1));
                }
                else
                {
                    // O tipte henüz hiç yorum yoksa her barda 0 görünsün, grafik çökmesin
                    veri.PozitifOranlari.Add(0); veri.NotrOranlari.Add(0); veri.NegatifOranlari.Add(0);
                }
            }

            return veri;
        }

        public AylikTrendGrafigiVerisi HesaplaAylikTrendGrafigi(List<Yorum> yorumlar)
        {
            if (yorumlar == null || !yorumlar.Any()) return new AylikTrendGrafigiVerisi();

            var trendVerisi = yorumlar.Select(y =>
            {
                DateTime parsedDate;
                bool isValid = DateTime.TryParse(y.MisafirKonaklamaTarihi, out parsedDate);
                if (!isValid && y.MisafirYorumTarihi.HasValue) { parsedDate = y.MisafirYorumTarihi.Value; isValid = true; }
                return new { IsValid = isValid, Date = parsedDate };
            })
                .Where(x => x.IsValid).GroupBy(x => x.Date.ToString("yyyy-MM"))
                .Select(g => new { Ay = g.Key, Yogunluk = g.Count() })
                .OrderBy(x => x.Ay).ToList();

            return new AylikTrendGrafigiVerisi
            {
                Aylar = trendVerisi.Select(x => x.Ay).ToList(),
                Yogunluklar = trendVerisi.Select(x => x.Yogunluk).ToList()
            };
        }

        public KpiKartlariVerisi HesaplaKpiKartlari(List<Yorum> tumYorumlar)
        {
            var bosVeri = new KpiKartlariVerisi();
            if (tumYorumlar == null || !tumYorumlar.Any()) return bosVeri;

            var analizliYorumlar = tumYorumlar.Where(y => y.GeminiAnalizYapildiMi == 1).ToList();
            int toplamYorum = analizliYorumlar.Count;

            if (toplamYorum == 0) return bosVeri;

            // DÜZELTME 1: Çarpı 2 (* 2) mantığı kaldırıldı. Gemini skoru 1-100 arası olduğu için 
            // ortalama zaten doğrudan % memnuniyeti verir.
            double toplamSkor = analizliYorumlar.Sum(y => y.GeminiAnalizDuyguSkoru ?? 0);
            double gercekOrtalama = toplamSkor / toplamYorum;
            int memnuniyetYuzdesi = (int)Math.Round(gercekOrtalama);

            // DÜZELTME 2: "pozitif" yerine yeni sistemdeki İyi ve Çok İyi kelimelerini arıyoruz
            int pozitifSayisi = analizliYorumlar.Count(y =>
                y.GeminiAnalizDuyguDurumu != null && (
                y.GeminiAnalizDuyguDurumu.Equals("Çok İyi", StringComparison.OrdinalIgnoreCase) ||
                y.GeminiAnalizDuyguDurumu.Equals("Çok Iyi", StringComparison.OrdinalIgnoreCase) ||
                y.GeminiAnalizDuyguDurumu.Equals("İyi", StringComparison.OrdinalIgnoreCase) ||
                y.GeminiAnalizDuyguDurumu.Equals("Iyi", StringComparison.OrdinalIgnoreCase)
            ));
            int pozitifOran = (int)Math.Round(((double)pozitifSayisi / toplamYorum) * 100);

            // DÜZELTME 3: "negatif" yerine yeni sistemdeki Kötü ve Çok Kötü kelimelerini arıyoruz
            int negatifSayisi = analizliYorumlar.Count(y =>
                y.GeminiAnalizDuyguDurumu != null && (
                y.GeminiAnalizDuyguDurumu.Equals("Kötü", StringComparison.OrdinalIgnoreCase) ||
                y.GeminiAnalizDuyguDurumu.Equals("Kotu", StringComparison.OrdinalIgnoreCase) ||
                y.GeminiAnalizDuyguDurumu.Equals("Çok Kötü", StringComparison.OrdinalIgnoreCase) ||
                y.GeminiAnalizDuyguDurumu.Equals("Çok Kotu", StringComparison.OrdinalIgnoreCase)
            ));
            int negatifOran = (int)Math.Round(((double)negatifSayisi / toplamYorum) * 100);

            return new KpiKartlariVerisi
            {
                ToplamYorumAdeti = toplamYorum,
                MemnuniyetOrani = memnuniyetYuzdesi,
                PozitifYorumOrani = pozitifOran,
                NegatifYorumOrani = negatifOran,
            };
        }
    }
}
