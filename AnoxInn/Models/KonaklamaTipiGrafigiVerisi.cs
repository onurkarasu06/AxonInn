namespace AxonInn.Models
{
    public class KonaklamaTipiGrafigiVerisi
    {
        public List<string> Tipler { get; set; } = new List<string>();
        public List<double> PozitifOranlari { get; set; } = new List<double>();
        public List<double> NotrOranlari { get; set; } = new List<double>();
        public List<double> NegatifOranlari { get; set; } = new List<double>();
    }
}
