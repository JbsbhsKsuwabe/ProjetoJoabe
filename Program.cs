using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<InventarioDb>(options =>
    options.UseSqlite("Data Source=inventario.db"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<InventarioDb>();
    db.Database.EnsureCreated();
}

app.UseDefaultFiles();
app.UseStaticFiles();

// --- ROTAS DA API DE PRODUTOS ---

app.MapGet("/api/produtos", async (string? busca, string? categoria, InventarioDb db) =>
{
    var query = db.Produtos.Include(p => p.Categoria).AsQueryable();

    if (!string.IsNullOrWhiteSpace(busca))
        query = query.Where(p => p.Nome.ToLower().Contains(busca.ToLower()));
    if (!string.IsNullOrWhiteSpace(categoria))
        query = query.Where(p => p.Categoria.Nome.ToLower().Contains(categoria.ToLower()));

    // Retorna um objeto anônimo formatado para facilitar a leitura no HTML
    return await query.Select(p => new {
        p.Id,
        p.Nome,
        Categoria = p.Categoria.Nome,
        p.Quantidade,
        p.Preco
    }).ToListAsync();
});

app.MapGet("/api/produtos/resumo", async (InventarioDb db) =>
{
    return await db.Produtos.Include(p => p.Categoria)
        .Select(p => new { p.Nome, Categoria = p.Categoria.Nome, p.Preco })
        .Distinct()
        .ToListAsync();
});

app.MapPost("/api/produtos", async (ProdutoDTO dto, InventarioDb db) =>
{
    // Verifica se o produto já existe pelo nome
    var produtoExistente = await db.Produtos.Include(p => p.Categoria)
        .FirstOrDefaultAsync(p => p.Nome.ToLower() == dto.Nome.ToLower());

    if (produtoExistente != null)
    {
        // Regra: Bloqueia se tentarem salvar com uma categoria diferente
        if (produtoExistente.Categoria.Nome.ToLower() != dto.NomeCategoria.ToLower())
        {
            return Results.BadRequest(new { mensagem = $"Erro: '{dto.Nome}' já está cadastrado na categoria '{produtoExistente.Categoria.Nome}'. Não é possível alterar a categoria ao adicionar estoque." });
        }

        // Soma a quantidade nova à existente
        produtoExistente.Quantidade += dto.Quantidade;
        await db.SaveChangesAsync();
        return Results.Ok(produtoExistente);
    }

    // Se o produto não existe, verifica se a categoria já existe. Se não, cria uma nova.
    var categoria = await db.Categorias.FirstOrDefaultAsync(c => c.Nome.ToLower() == dto.NomeCategoria.ToLower());
    if (categoria == null)
    {
        categoria = new Categoria { Nome = dto.NomeCategoria };
        db.Categorias.Add(categoria);
    }

    var novoProduto = new Produto
    {
        Nome = dto.Nome,
        Categoria = categoria,
        Quantidade = dto.Quantidade,
        Preco = dto.Preco
    };

    db.Produtos.Add(novoProduto);
    await db.SaveChangesAsync();
    return Results.Created($"/api/produtos/{novoProduto.Id}", novoProduto);
});

app.MapPut("/api/produtos/{id}", async (int id, ProdutoDTO dto, InventarioDb db) =>
{
    var produto = await db.Produtos.Include(p => p.Categoria).FirstOrDefaultAsync(p => p.Id == id);
    if (produto is null) return Results.NotFound();

    // Atualiza ou cria a nova categoria caso editada
    var categoria = await db.Categorias.FirstOrDefaultAsync(c => c.Nome.ToLower() == dto.NomeCategoria.ToLower());
    if (categoria == null)
    {
        categoria = new Categoria { Nome = dto.NomeCategoria };
        db.Categorias.Add(categoria);
    }

    produto.Nome = dto.Nome;
    produto.Categoria = categoria;
    produto.Quantidade = dto.Quantidade;
    produto.Preco = dto.Preco;

    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapDelete("/api/produtos/{id}", async (int id, InventarioDb db) =>
{
    if (await db.Produtos.FindAsync(id) is Produto produto)
    {
        db.Produtos.Remove(produto);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }
    return Results.NotFound();
});


// --- ROTAS DA API DE VENDAS ---

app.MapGet("/api/vendas", async (DateTime? data, string? categoria, string? nome, InventarioDb db) =>
{
    var query = db.Vendas.Include(v => v.Produto).ThenInclude(p => p.Categoria).AsQueryable();

    if (data.HasValue) query = query.Where(v => v.DataVenda.Date == data.Value.Date);
    if (!string.IsNullOrWhiteSpace(categoria)) query = query.Where(v => v.Produto.Categoria.Nome.ToLower().Contains(categoria.ToLower()));
    if (!string.IsNullOrWhiteSpace(nome)) query = query.Where(v => v.Produto.Nome.ToLower().Contains(nome.ToLower()));

    // Retorna formatado para eliminar a necessidade de buscar a categoria no HTML
    return await query.OrderByDescending(v => v.DataVenda).Select(v => new {
        v.Id,
        NomeProduto = v.Produto.Nome,
        Categoria = v.Produto.Categoria.Nome,
        v.Quantidade,
        v.ValorTotal,
        v.DataVenda
    }).ToListAsync();
});

app.MapPost("/api/vendas", async (VendaDTO dto, InventarioDb db) =>
{
    var produto = await db.Produtos.FindAsync(dto.ProdutoId);
    if (produto == null) return Results.NotFound("Produto não encontrado.");
    if (produto.Quantidade < dto.Quantidade)
        return Results.BadRequest(new { mensagem = $"Estoque insuficiente. Restam {produto.Quantidade} unidades." });

    produto.Quantidade -= dto.Quantidade;

    // Venda agora guarda apenas o ID do produto para evitar redundância
    var venda = new Venda
    {
        ProdutoId = produto.Id,
        Quantidade = dto.Quantidade,
        ValorTotal = produto.Preco * dto.Quantidade,
        DataVenda = dto.DataVenda
    };

    db.Vendas.Add(venda);
    await db.SaveChangesAsync();
    return Results.Ok(venda);
});

app.Run();

// --- MODELOS DE BANCO DE DADOS NORMALIZADOS ---

public class Categoria
{
    public int Id { get; set; }
    public string Nome { get; set; } = string.Empty;
}

public class Produto
{
    public int Id { get; set; }
    public string Nome { get; set; } = string.Empty;
    public int CategoriaId { get; set; } // Chave Estrangeira
    public Categoria Categoria { get; set; } = null!; // Navegação
    public int Quantidade { get; set; }
    public decimal Preco { get; set; }
}

public class Venda
{
    public int Id { get; set; }
    public int ProdutoId { get; set; } // Chave Estrangeira
    public Produto Produto { get; set; } = null!; // Navegação
    public int Quantidade { get; set; }
    public decimal ValorTotal { get; set; }
    public DateTime DataVenda { get; set; }
}

// --- DTOs (Transferência de Dados) ---
public class ProdutoDTO
{
    public string Nome { get; set; } = string.Empty;
    public string NomeCategoria { get; set; } = string.Empty;
    public int Quantidade { get; set; }
    public decimal Preco { get; set; }
}

public class VendaDTO
{
    public int ProdutoId { get; set; }
    public int Quantidade { get; set; }
    public DateTime DataVenda { get; set; }
}

public class InventarioDb : DbContext
{
    public InventarioDb(DbContextOptions<InventarioDb> options) : base(options) { }
    public DbSet<Categoria> Categorias => Set<Categoria>();
    public DbSet<Produto> Produtos => Set<Produto>();
    public DbSet<Venda> Vendas => Set<Venda>();
}