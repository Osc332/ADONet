# Билет 4 — «Библиотека: книги и выдачи» (решение)

## Задание

Реализовать в одном Minimal API-проекте все темы ADO.NET:

| # | Эндпоинт | Темы |
|---|----------|------|
| 1 | `POST /loans` | соединение, команда, параметры, транзакция, `FOR UPDATE`, async, пул |
| 2 | `GET /books/search?q=` | Dapper, параметризация, async, пул |
| 3 | `GET /loans/report` | `DataSet` + `DataAdapter` + `DataTable` + `DataRelation` |

---

## Структура проекта

```
MyApp/
├── MyApp.csproj
├── Dockerfile
├── docker-compose.yml
├── init.sql
└── Program.cs
```

---

## `MyApp.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>MyApp</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Npgsql" Version="8.0.5" />
    <PackageReference Include="Dapper" Version="2.1.35" />
  </ItemGroup>
</Project>
```

## `Dockerfile`

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80
ENTRYPOINT ["dotnet", "MyApp.dll"]
```

## `docker-compose.yml`

```yaml
version: '3.8'
services:
  db:
    image: postgres:16
    environment:
      POSTGRES_DB: shopdb
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: postgres
    ports: ["5432:5432"]
    volumes:
      - ./init.sql:/docker-entrypoint-initdb.d/init.sql
  app:
    build: .
    environment:
      CONN_STRING: "Host=db;Port=5432;Database=shopdb;Username=postgres;Password=postgres;Pooling=true;Minimum Pool Size=1;Maximum Pool Size=20"
    ports: ["8080:80"]
    depends_on: [db]
```

## `init.sql`

```sql
CREATE TABLE books (
    id SERIAL PRIMARY KEY,
    title VARCHAR(150) NOT NULL,
    copies INT NOT NULL
);
CREATE TABLE loans (
    id SERIAL PRIMARY KEY,
    book_id INT REFERENCES books(id),
    reader VARCHAR(100) NOT NULL
);
INSERT INTO books (title, copies) VALUES
    ('books about cats', 3),
    ('book about nature', 2);
```

---

## `Program.cs` — полное решение

```csharp
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
```

---

## Разбор ключевых тем по билету

### 1. Соединение и пул
- `NpgsqlConnection` берётся из **пула** — `Pooling=true` уже стоит в `CONN_STRING`.
- `await using` гарантирует возврат соединения в пул даже при исключении.
- `conn.OpenAsync()` — открытие **не создаёт новое TCP-соединение**, а берёт готовое из пула (время ~микросекунды).

### 2. Команды и параметры
- `NpgsqlCommand` — отправляет SQL в PostgreSQL.
- Параметры `@id`, `@book_id`, `@reader` — защита от SQL-инъекций.
- `ExecuteScalarAsync` — для `SELECT ... RETURNING id` и `COUNT(*)`.
- Параметризация в Dapper — через анонимный объект `new { q = $"%{q}%" }`.

### 3. Транзакция
- `BeginTransactionAsync` → набор операций должен быть атомарным.
- `CommitAsync()` — фиксация.
- `RollbackAsync()` — откат при ошибке (например, нет копий).
- **`FOR UPDATE`** в `SELECT copies ...` — блокировка строки книги; конкурирующие запросы ждут завершения транзакции → защита от переполнения выдачи.

### 4. Async/await
- Все I/O операции — `*Async`: `OpenAsync`, `ExecuteScalarAsync`, `QueryAsync`, `CommitAsync`, `RollbackAsync`.
- Эндпоинты объявлены как `async` — thread из пула ASP.NET Core не блокируется на время запроса к БД.

### 5. DataSet / DataAdapter / DataTable / DataRelation
- **`DataTable`** — in-memory таблица, полностью независима от БД.
- **`NpgsqlDataAdapter`** — мост: выполняет SQL и наполняет `DataTable` (`Fill`).
- **`DataSet`** — контейнер для нескольких `DataTable` + связей.
- **`DataRelation`** — связывает `Books.id` ↔ `Loans.book_id`, позволяет получать дочерние строки через `GetChildRows("Books_Loans")`.

### 6. Dapper
- Одна строка `conn.QueryAsync<Book>(sql, new {q})` заменяет открытие команды + параметры + ридер + маппинг.
- Маппинг в `record Book` — по именам столбцов (id→Id, title→Title, copies→Copies).

---

## Проверка работы

```bash
docker compose up --build -d
```

```bash
# 1) Поиск книг
curl "http://localhost:8080/books/search?q=cats"

# 2) Выдача (book 1 = "book about cats", copies=3)
curl -X POST http://localhost:8080/loans \
     -H "Content-Type: application/json" \
     -d '{"bookId":1,"reader":"Andrew"}'

# 3) Превышение копий (book 2, copies=2 — после 3 выдач откат)
curl -X POST http://localhost:8080/loans \
     -H "Content-Type: application/json" \
     -d '{"bookId":2,"reader":"Jacob"}'
# → 400 Bad Request

# 4) Отчёт DataSet + DataRelation
curl http://localhost:8080/loans/report
```

---

## Чек-лист по заданию

| Тема | Где реализовано |
|------|----------------|
| Соединение | `new NpgsqlConnection(cs)` + `Open/OpenAsync` |
| Команда | `NpgsqlCommand` в `POST /loans`, `NpgsqlDataAdapter` в отчёте |
| Параметры | `AddWithValue` (3 команды) + Dapper `new { q }` |
| Транзакция | `BeginTransactionAsync` / `Commit` / `Rollback` + `FOR UPDATE` |
| Async | `OpenAsync`, `ExecuteScalarAsync`, `QueryAsync`, `CommitAsync` |
| Пул | `Pooling=true` в `CONN_STRING`, `await using` возвращает соединение |
| Dapper | `GET /books/search` — `conn.QueryAsync<Book>` |
| DataSet | `new DataSet("Library")` + `ds.Relations.Add(...)` |
| DataAdapter | `NpgsqlDataAdapter(...).Fill(table)` ×2 |
| DataTable | `Books`, `Loans` + `GetChildRows` |
| DataRelation | `new DataRelation("Books_Loans", ...)` |
