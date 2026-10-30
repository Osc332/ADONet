# Отчёт: реализация и запуск проекта «Магазин автозапчастей» на Docker

**Студент:** Черноморов Георгий
**Группа:** РПО 3
**Дата:** 23.10.2026
**Вариант:** Магазин автозапчастей (C# ASP.NET Core MVC + PostgreSQL + Docker)

---

## 1. Цель работы

- Освоить контейнеризацию ASP.NET Core-приложения и базы данных PostgreSQL через `docker-compose`.
- Реализовать слой доступа к данным (`DbHelper`) на ADO.NET через провайдер `Npgsql`.
- Проверить работоспособность CRUD-сценариев и атомарного уменьшения остатка.

---

## 2. Исходные данные

Проект `autoparts-docker/` содержал:

- `docker-compose.yml` (сервисы `db` и `web`, healthcheck через `pg_isready`);
- `Dockerfile` (multi-stage: `sdk:8.0` → `aspnet:8.0`);
- `AutoPartsApp.csproj` с пакетом `Npgsql 8.0.5`;
- модель `Part`, контроллер `PartsController`, представления `Index / Details / Create`;
- `Program.cs` с инициализацией БД и ретраями;
- **пустой каркас `Data/DbHelper.cs`** — все методы бросали `NotImplementedException`.

Требовалось реализовать пять методов `DbHelper` и убедиться, что приложение собирается и работает в Docker.

---

## 3. Что было сделано

### 3.1. Реализация `Data/DbHelper.cs`

Реализованы все пять методов.

| Метод | Что делает | Ключевые моменты |

| `Initialize()` | Создаёт таблицу `parts` и сидит тестовые данные | `CREATE TABLE IF NOT EXISTS`, затем `SELECT COUNT(*)`; сидинг только если `count == 0` (идемпотентно) |

| `GetAllParts()` | Возвращает все запчасти | `SELECT ... ORDER BY id`, чтение через `NpgsqlDataReader`, `IsDBNull(6)` для `description` |

| `GetPartById(int id)` | Возвращает одну запчасть или `null` | Параметризованный `WHERE id = @id`, `AddWithValue` |

| `AddPart(Part part)` | Вставляет новую запчасть | `INSERT` с параметрами, `(object?)part.Description ?? DBNull.Value` |

| `DecreaseStock(int partId, int quantity)` | Атомарно уменьшает остаток | `UPDATE ... WHERE id = @id AND stock >= @qty`, `return ExecuteNonQuery() > 0` |

**Фрагмент `Initialize()`:**

public void Initialize()
{
    using var connection = new NpgsqlConnection(_connectionString);
    connection.Open();

    using (var createCmd = new NpgsqlCommand(@"
        CREATE TABLE IF NOT EXISTS parts (
            id           SERIAL PRIMARY KEY,
            name         VARCHAR(200) NOT NULL,
            article      VARCHAR(50)  NOT NULL,
            manufacturer VARCHAR(100) NOT NULL,
            price        NUMERIC(18,2) NOT NULL,
            stock        INTEGER NOT NULL DEFAULT 0,
            description  VARCHAR(500)
        )", connection))
    {
        createCmd.ExecuteNonQuery();
    }

    using var checkCmd = new NpgsqlCommand("SELECT COUNT(*) FROM parts", connection);
    var count = Convert.ToInt32(checkCmd.ExecuteScalar());

    if (count == 0)
    {
        using var seedCmd = new NpgsqlCommand(@"
            INSERT INTO parts (name, article, manufacturer, price, stock, description) VALUES
            ('Тормозная колодка', 'BC-1234', 'Bosch', 2500.00, 40, 'Передняя, для легковых авто'),
            ('Масляный фильтр',  'OF-5678', 'Mann',   850.00, 120, 'Универсальный'),
            ('Свеча зажигания',  'SP-9012', 'NGK',    450.00, 200, 'Иридиевая')", connection);
        seedCmd.ExecuteNonQuery();
    }
}

**Скриншоты**




<img width="758" height="322" alt="Снимок экрана 2026-10-24 010134" src="https://github.com/user-attachments/assets/e20e6feb-09bc-435c-96be-e2fb29316c5a" />
<img width="961" height="178" alt="Снимок экрана 2026-10-24 010956" src="https://github.com/user-attachments/assets/047e5385-a40d-4170-9059-f12829013b5d" />
<img width="959" height="147" alt="Снимок экрана 2026-10-24 011001" src="https://github.com/user-attachments/assets/7865ca2d-9817-4ea9-a588-bd7e303d5d74" />
