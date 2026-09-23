using Microsoft.EntityFrameworkCore;
using QuinielaBackend.Models;

namespace QuinielaBackend.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Sorteo> Sorteos { get; set; }
        public DbSet<PosicionSorteo> Posiciones { get; set; }
    }
}

