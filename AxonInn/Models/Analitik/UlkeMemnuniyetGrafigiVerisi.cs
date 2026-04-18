namespace AxonInn.Models.Analitik
{
    public class UlkeMemnuniyetGrafigiVerisi
    {
        public List<string> Ulkeler { get; set; } = new List<string>();
        public List<double> PozitifOranlari { get; set; } = new List<double>();
        public List<double> NotrOranlari { get; set; } = new List<double>();
        public List<double> NegatifOranlari { get; set; } = new List<double>();
    }
}
