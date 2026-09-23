namespace QuinielaBackend.Models
{
    public class Sorteo
    {
        public int Id { get; set; }
        public DateTime Fecha { get; set; }
        public string TipoSorteo { get; set; } = string.Empty;
        public List<PosicionSorteo> Posiciones { get; set; } = new();
    }

    public class PosicionSorteo
    {
        public int Id { get; set; }
        public int SorteoId { get; set; }
        public int Posicion { get; set; }
        public string Numero { get; set; } = string.Empty;
    }
}