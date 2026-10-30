using System.Data;
using Dapper;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
string cs = Environment.GetEnvironmentVariable("CONN_STRING")!;

app.MapPost("/loans", async (LoanReq l) =>
{
    await using var conn = new NpgsqlConnection(cs);
    await conn.OpenAsync();

    await using var tx = await conn.BeginTransactionAsync();
    try
    {
        await using var lockCmd = new NpgsqlCommand(
            "SELECT copies FROM books WHERE id = @id FOR UPDATE", conn, tx);
        lockCmd.Parameters.AddWithValue("id", l.BookId);

        var copiesObj = await lockCmd.ExecuteScalarAsync();
        if (copiesObj is null or DBNull)
        {
            await tx.RollbackAsync();
            return Results.NotFound($"Book {l.BookId} not found");
        }
        int copies = Convert.ToInt32(copiesObj);

        await using var countCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM loans WHERE book_id = @id", conn, tx);
        countCmd.Parameters.AddWithValue("id", l.BookId);
        int taken = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

        if (taken >= copies)
        {
            await tx.RollbackAsync();
            return Results.BadRequest($"No available copies for book {l.BookId}");
        }

        await using var insertCmd = new NpgsqlCommand(
            "INSERT INTO loans (book_id, reader) VALUES (@book_id, @reader) RETURNING id",
            conn, tx);
        insertCmd.Parameters.AddWithValue("book_id", l.BookId);
        insertCmd.Parameters.AddWithValue("reader", l.Reader);
        int newId = Convert.ToInt32(await insertCmd.ExecuteScalarAsync());

        await tx.CommitAsync();
        return Results.Ok(new { id = newId, bookId = l.BookId, reader = l.Reader });
    }
    catch
    {
        await tx.RollbackAsync();
        throw;
    }
});

app.MapGet("/books/search", async (string q) =>
{
    await using var conn = new NpgsqlConnection(cs);
    await conn.OpenAsync();

    var books = await conn.QueryAsync<Book>(
        "SELECT id, title, copies FROM books WHERE title ILIKE @q ORDER BY title",
        new { q = $"%{q}%" });

    return Results.Ok(books);
});

app.MapGet("/loans/report", () =>
{
    var ds = new DataSet("Library");

    using var conn = new NpgsqlConnection(cs);
    conn.Open();

    using (var adapter = new NpgsqlDataAdapter(
        "SELECT id, title, copies FROM books ORDER BY id", conn))
    {
        var booksTable = new DataTable("Books");
        adapter.Fill(booksTable);
        ds.Tables.Add(booksTable);
    }

    using (var adapter = new NpgsqlDataAdapter(
        "SELECT id, book_id, reader FROM loans ORDER BY id", conn))
    {
        var loansTable = new DataTable("Loans");
        adapter.Fill(loansTable);
        ds.Tables.Add(loansTable);
    }

    var relation = new DataRelation(
        "Books_Loans",
        ds.Tables["Books"]!.Columns["id"],
        ds.Tables["Loans"]!.Columns["book_id"]);
    ds.Relations.Add(relation);

    var report = ds.Tables["Books"]!.AsEnumerable().Select(b => new
    {
        id     = (int)b["id"],
        title  = (string)b["title"],
        copies = (int)b["copies"],
        loans  = b.GetChildRows("Books_Loans").Select(cr => new
        {
            id     = (int)cr["id"],
            reader = (string)cr["reader"]
        }).ToList()
    });

    return Results.Ok(new
    {
        dataset   = ds.DataSetName,
        tables    = ds.Tables.Cast<DataTable>().Select(t => t.TableName),
        relations = ds.Relations.Cast<DataRelation>().Select(r => r.RelationName),
        books     = report
    });
});

app.Run();

record LoanReq(int BookId, string Reader);
record Book(int Id, string Title, int Copies);