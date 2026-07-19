using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.Sqlite;

class Program
{
    static string TG_TOKEN = Environment.GetEnvironmentVariable("TG_TOKEN")!;
    static string WEATHER_KEY = Environment.GetEnvironmentVariable("WEATHER_KEY")!;

    static TelegramBotClient bot = null!;
    static HttpClient http = new HttpClient();
    static Database db = null!;

    static readonly Dictionary<string, string> CurrencyNames = new()
    {
        ["RUB"] = "🇷🇺 Рубль",
        ["KZT"] = "🇰🇿 Тенге",
        ["USD"] = "🇺🇸 Доллар",
        ["EUR"] = "🇪🇺 Евро",
        ["GBP"] = "🇬🇧 Фунт",
        ["CNY"] = "🇨🇳 Юань",
        ["AED"] = "🇦🇪 Дирхам",
        ["TRY"] = "🇹🇷 Лира",
        ["UAH"] = "🇺🇦 Гривна",
        ["KGS"] = "🇰🇬 Сом",
        ["UZS"] = "🇺🇿 Сум",
        ["PLN"] = "🇵🇱 Злотый"
    };

    static readonly string[] WorldCitiesEn = { "London", "Tokyo", "Berlin", "Paris", "Rome", "Madrid", "Beijing", "Sydney", "New York", "Toronto", "Seoul", "Bangkok", "Dubai", "Singapore", "Mumbai" };
    static readonly Random rng = new();

    static ConcurrentDictionary<long, UserState> users = new();
    static ConcurrentDictionary<long, int> subscribers = new();

    static async Task Main()
    {
        db = new Database();
        db.Initialize();

        var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseUrls($"http://*:{port}");
                webBuilder.Configure(app => app.Run(async context =>
                    await context.Response.WriteAsync("Bot is running")));
            })
            .Build();
        _ = host.RunAsync();

        bot = new TelegramBotClient(TG_TOKEN);
        var cts = new CancellationTokenSource();

        bot.StartReceiving(HandleUpdate, HandleError,
            new ReceiverOptions
            {
                AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery, UpdateType.InlineQuery }
            }, cts.Token);

        var me = await bot.GetMe();
        Console.WriteLine($"Бот @{me.Username} запущен!");

        _ = Task.Run(() => DailyNotifyLoop(cts.Token), cts.Token);
        _ = Task.Run(() => DangerousWeatherLoop(cts.Token), cts.Token);

        await Task.Delay(Timeout.Infinite, cts.Token);
    }

    static async Task HandleUpdate(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        if (update.InlineQuery != null) { await HandleInlineQuery(botClient, update.InlineQuery, ct); return; }
        if (update.CallbackQuery != null) { await HandleCallback(botClient, update.CallbackQuery, ct); return; }
        if (update.Message?.Location != null) { await HandleLocation(botClient, update.Message, ct); return; }
        if (update.Message?.Text == null) return;

        var chatId = update.Message.Chat.Id;
        var text = update.Message.Text.Trim();
        var user = users.GetOrAdd(chatId, _ => db.LoadUser(chatId) ?? new UserState());
        user.RequestCount++;
        try { await bot.DeleteMessage(chatId, update.Message.MessageId, ct); } catch { }

        if (text.StartsWith("/remind"))
        {
            var parts = text.Split(' ', 2);
            if (parts.Length == 2 && TimeSpan.TryParse(parts[1], out var ts))
            {
                user.RemindTime = ts.ToString(@"hh\:mm");
                db.SaveUser(chatId, user);
                await EditOrSendMain(chatId, user, ct, extra: "⏰ Напоминание установлено на " + user.RemindTime);
            }
            else await EditOrSendMain(chatId, user, ct, extra: "Формат: /remind 08:00");
            return;
        }
        if (text == "/stats")
        {
            var statsMsg = $"📊 Статистика:\nЗапросов: {user.RequestCount}\nГород: {user.CityRu}\nПодписка: {(user.Subscribed ? "активна" : "нет")}";
            await EditOrSendMain(chatId, user, ct, extra: statsMsg);
            return;
        }

        if (user.WaitingForCustomCity)
        {
            user.WaitingForCustomCity = false;
            var (ok, _) = await TryGetWeather(text);
            if (!ok) { await EditOrSendMain(chatId, user, ct, extra: $"❌ Город \"{text}\" не найден."); return; }
            user.CityEn = text; user.CityRu = text;
            db.SaveUser(chatId, user);
            await EditMainMenu(chatId, user, ct);
            return;
        }
        if (user.WaitingForAmount)
        {
            user.WaitingForAmount = false;
            await HandleAmountInput(chatId, user, text, ct);
            return;
        }
        await EditMainMenu(chatId, user, ct);
    }

    static async Task HandleLocation(ITelegramBotClient botClient, Message msg, CancellationToken ct)
    {
        var lat = msg.Location!.Latitude; var lon = msg.Location.Longitude;
        var user = users.GetOrAdd(msg.Chat.Id, _ => new UserState());
        try
        {
            var geoUrl = $"https://api.openweathermap.org/data/2.5/weather?lat={lat}&lon={lon}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(geoUrl));
            var city = json["name"]?.ToString();
            if (!string.IsNullOrEmpty(city)) { user.CityEn = city; user.CityRu = city; db.SaveUser(msg.Chat.Id, user); await EditMainMenu(msg.Chat.Id, user, ct); }
            else await EditOrSendMain(msg.Chat.Id, user, ct, extra: "❌ Не удалось определить город.");
        }
        catch { await EditOrSendMain(msg.Chat.Id, user, ct, extra: "❌ Ошибка геолокации."); }
    }

    static async Task HandleCallback(ITelegramBotClient botClient, CallbackQuery query, CancellationToken ct)
    {
        var chatId = query.Message!.Chat.Id;
        var data = query.Data!;
        var user = users.GetOrAdd(chatId, _ => new UserState());
        user.MainMessageId = query.Message.MessageId;
        user.RequestCount++;
        await botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        switch (data)
        {
            case "refresh": case "back": await EditMainMenu(chatId, user, ct); break;
            case "forecast": await AnimateLoading(chatId, user, ct, async () => await EditForecast(chatId, user, ct)); break;
            case "rates": await AnimateLoading(chatId, user, ct, async () => await EditRates(chatId, user, ct)); break;
            case "convert": user.ConvertFrom = null; user.ConvertTo = null; user.WaitingForAmount = false; await EditConvertFromMenu(chatId, user, ct); break;
            case "help": await EditHelp(chatId, user, ct); break;
            case "map": await EditMapLink(chatId, user, ct); break;
            case "subscribe":
                user.Subscribed = !user.Subscribed;
                db.SaveUser(chatId, user);
                if (user.Subscribed) subscribers[chatId] = GetUtcOffset(user.CityEn);
                else subscribers.TryRemove(chatId, out _);
                await EditMainMenu(chatId, user, ct);
                break;
            case "favorite":
                if (!user.Favorites.Contains(user.CityEn)) { user.Favorites.Add(user.CityEn); if (user.Favorites.Count > 3) user.Favorites.RemoveAt(0); db.SaveFavorites(chatId, user.Favorites); }
                await EditMainMenu(chatId, user, ct);
                break;
            case "unfavorite": user.Favorites.Remove(user.CityEn); db.SaveFavorites(chatId, user.Favorites); await EditMainMenu(chatId, user, ct); break;
            case "today": await AnimateLoading(chatId, user, ct, async () => await EditToday(chatId, user, ct)); break;
            case "hourly": await AnimateLoading(chatId, user, ct, async () => await EditHourly(chatId, user, ct)); break;
            case "history": await EditHistory(chatId, user, ct); break;
            case "share": await ShareWeather(chatId, user, ct); break;

            case "city_almetyevsk": SetCity(user, "Almetyevsk", "Альметьевск", chatId); await EditMainMenu(chatId, user, ct); break;
            case "city_shymkent":   SetCity(user, "Shymkent", "Шымкент", chatId);     await EditMainMenu(chatId, user, ct); break;
            case "city_moscow":     SetCity(user, "Moscow", "Москва", chatId);         await EditMainMenu(chatId, user, ct); break;
            case "city_almaty":     SetCity(user, "Almaty", "Алматы", chatId);         await EditMainMenu(chatId, user, ct); break;
            case "city_astana":     SetCity(user, "Astana", "Астана", chatId);         await EditMainMenu(chatId, user, ct); break;
            case "city_custom":
                user.WaitingForCustomCity = true;
                await EditOrSendMain(chatId, user, ct, extra: "✏️ Введите название города на английском:",
                    keyboard: new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Отмена", "back") } }));
                break;
            case "city_random":
                var randomCity = WorldCitiesEn[rng.Next(WorldCitiesEn.Length)];
                SetCity(user, randomCity, randomCity, chatId);
                await EditMainMenu(chatId, user, ct);
                break;
            case "choose_city": await EditCityMenu(chatId, user, ct); break;

            case string s when s.StartsWith("convfrom_"):
                user.ConvertFrom = s["convfrom_".Length..];
                await EditConvertToMenu(chatId, user, ct);
                break;
            case string s when s.StartsWith("convto_"):
                user.ConvertTo = s["convto_".Length..];
                user.WaitingForAmount = true;
                await EditOrSendMain(chatId, user, ct,
                    extra: $"🧮 Введите сумму ({CurrencyName(user.ConvertFrom!)} → {CurrencyName(user.ConvertTo!)}):",
                    keyboard: new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Отмена", "back") } }));
                break;
            case string s when s.StartsWith("fav_"):
                var favCity = s[4..];
                SetCity(user, favCity, favCity, chatId);
                await EditMainMenu(chatId, user, ct);
                break;
        }
    }

    static string CurrencyName(string code) => CurrencyNames.TryGetValue(code, out var name) ? name : code;
    static void SetCity(UserState user, string en, string ru, long chatId) { user.CityEn = en; user.CityRu = ru; db.SaveUser(chatId, user); }

    // Главное меню (без новостей и графика)
    static async Task EditMainMenu(long chatId, UserState user, CancellationToken ct)
    {
        var (weatherText, temp) = await GetWeather(user.CityEn, user.CityRu);
        var (sunrise, sunset) = await GetSunriseSunset(user.CityEn);
        var timeStr = GetLocalTimeString(user.CityEn, user.CityRu);

        string greeting = "";
        if (user.FirstRun) { greeting = "👋 Добро пожаловать, брат!\n\n"; user.FirstRun = false; }

        string tempHint = "";
        if (temp.HasValue)
        {
            double t = temp.Value;
            if (t <= -10) tempHint = "\n🥶 Очень холодно!";
            else if (t >= 30) tempHint = "\n🥵 Жарко!";
            if (user.LastTempDate.Date == DateTime.Now.AddDays(-1).Date && user.LastTemp.HasValue)
            {
                var diff = t - user.LastTemp.Value;
                if (Math.Abs(diff) >= 5)
                    tempHint += $"\n📈 По сравнению со вчерашним днём: {diff:+0;-0}F0°C";
            }
            user.LastTemp = t;
            user.LastTempDate = DateTime.Now;
            db.SaveUser(chatId, user);
        }

        var sunriseLine = sunrise != "" ? $"\n🌅 Восход: {sunrise}   🌇 Закат: {sunset}" : "";

        var text = $"{greeting}🏙 *{user.CityRu}*\n{timeStr}{sunriseLine}\n\n{weatherText}{tempHint}\n\nВыберите действие:";

        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("📆 Прогноз", "forecast"), InlineKeyboardButton.WithCallbackData("💵 Курсы", "rates") },
            new[] { InlineKeyboardButton.WithCallbackData("🧮 Конвертер", "convert"), InlineKeyboardButton.WithCallbackData("🌍 Город", "choose_city") },
            new[] { InlineKeyboardButton.WithCallbackData("📍 Карта", "map"), InlineKeyboardButton.WithCallbackData("❓ Помощь", "help") },
            new[] { InlineKeyboardButton.WithCallbackData("📤 Поделиться", "share"), InlineKeyboardButton.WithCallbackData(user.Subscribed ? "🔕 Отписаться" : "🔔 Подписаться", "subscribe") },
        };

        if (user.Favorites.Count > 0)
        {
            var favRow = user.Favorites.Select(f => InlineKeyboardButton.WithCallbackData(f, $"fav_{f}")).ToArray();
            rows.Insert(0, favRow);
        }
        var utilRow = new List<InlineKeyboardButton>
        {
            user.Favorites.Contains(user.CityEn)
                ? InlineKeyboardButton.WithCallbackData("⭐ Убрать из избранного", "unfavorite")
                : InlineKeyboardButton.WithCallbackData("⭐ В избранное", "favorite")
        };
        rows.Add(utilRow.ToArray());

        await EditOrSendMessage(chatId, user, text, new InlineKeyboardMarkup(rows), ct);
    }

    // Справка без новостей и графиков
    static async Task EditHelp(long chatId, UserState user, CancellationToken ct)
    {
        var help = "❓ *Справка:*\n\n• 🌤 Погода и время\n• 📆 Прогноз на 5 дней\n• 💵 Курсы валют\n• 🧮 Конвертер\n• 🌍 Выбор города\n• 🔔 Подписка\n• 📍 Карта\n• 📊 /stats\n• ⏰ /remind 08:00";
        var keyboard = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") } });
        await EditOrSendMessage(chatId, user, help, keyboard, ct);
    }

    // ... (все остальные методы: EditForecast, EditToday, EditHourly, ShareWeather, GetSunriseSunset, GetRates, EditRates, EditConvertFromMenu, EditConvertToMenu, EditHistory, HandleAmountInput, AnimateLoading, DailyNotifyLoop, DangerousWeatherLoop, GetTimeZone, GetLocalTimeString, HandleInlineQuery, EditOrSendMessage, EditOrSendMain, TryGetWeather, GetWeather, GetTodayForecast, EditCityMenu, EditMapLink, Database и UserState) ОСТАВЛЯЕМ БЕЗ ИЗМЕНЕНИЙ, как в последней полной версии.
}

// Классы UserState и Database (оставляем как есть, они не изменились)
class UserState
{
    public string CityEn = "Almetyevsk";
    public string CityRu = "Альметьевск";
    public bool WaitingForCustomCity;
    public bool Subscribed;
    public int MainMessageId;
    public bool FirstRun = true;

    public string? ConvertFrom;
    public string? ConvertTo;
    public bool WaitingForAmount;

    public int RequestCount;
    public double? LastTemp;
    public DateTime LastTempDate;

    public List<string> Favorites = new();
    public List<string> ConversionHistory = new();
    public string? RemindTime;
    public DateTime LastNotifyDate;
}

class Database
{
    readonly string connectionString = "Data Source=/data/bot.db";

    public void Initialize()
    {
        Directory.CreateDirectory("/data");
        using var con = new SqliteConnection(connectionString);
        con.Open();
        var cmd = con.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Users (
                ChatId INTEGER PRIMARY KEY,
                CityEn TEXT,
                CityRu TEXT,
                Subscribed INTEGER,
                RemindTime TEXT,
                LastTemp REAL,
                LastTempDate TEXT
            );
            CREATE TABLE IF NOT EXISTS Favorites (
                ChatId INTEGER,
                CityEn TEXT,
                PRIMARY KEY (ChatId, CityEn)
            );
            CREATE TABLE IF NOT EXISTS ConversionHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ChatId INTEGER,
                EntryText TEXT,
                CreatedAt TEXT
            );
        ";
        cmd.ExecuteNonQuery();
    }

    public UserState? LoadUser(long chatId)
    {
        using var con = new SqliteConnection(connectionString);
        con.Open();
        var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT CityEn, CityRu, Subscribed, RemindTime, LastTemp, LastTempDate FROM Users WHERE ChatId = @id";
        cmd.Parameters.AddWithValue("@id", chatId);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var st = new UserState
            {
                CityEn = reader.GetString(0),
                CityRu = reader.GetString(1),
                Subscribed = reader.GetInt32(2) == 1,
                RemindTime = reader.IsDBNull(3) ? null : reader.GetString(3),
                LastTemp = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                LastTempDate = reader.IsDBNull(5) ? DateTime.MinValue : DateTime.Parse(reader.GetString(5))
            };
            cmd.CommandText = "SELECT CityEn FROM Favorites WHERE ChatId = @id";
            using var favReader = cmd.ExecuteReader();
            while (favReader.Read()) st.Favorites.Add(favReader.GetString(0));
            return st;
        }
        return null;
    }

    public void SaveUser(long chatId, UserState user)
    {
        using var con = new SqliteConnection(connectionString);
        con.Open();
        var cmd = con.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO Users (ChatId, CityEn, CityRu, Subscribed, RemindTime, LastTemp, LastTempDate)
            VALUES (@id, @cityEn, @cityRu, @sub, @remind, @temp, @tempDate)";
        cmd.Parameters.AddWithValue("@id", chatId);
        cmd.Parameters.AddWithValue("@cityEn", user.CityEn);
        cmd.Parameters.AddWithValue("@cityRu", user.CityRu);
        cmd.Parameters.AddWithValue("@sub", user.Subscribed ? 1 : 0);
        cmd.Parameters.AddWithValue("@remind", (object?)user.RemindTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@temp", (object?)user.LastTemp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tempDate", user.LastTempDate == DateTime.MinValue ? DBNull.Value : user.LastTempDate.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public void SaveFavorites(long chatId, List<string> favorites)
    {
        using var con = new SqliteConnection(connectionString);
        con.Open();
        var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM Favorites WHERE ChatId = @id";
        cmd.Parameters.AddWithValue("@id", chatId);
        cmd.ExecuteNonQuery();
        foreach (var fav in favorites)
        {
            cmd.CommandText = "INSERT INTO Favorites (ChatId, CityEn) VALUES (@id, @city)";
            cmd.Parameters.AddWithValue("@city", fav);
            cmd.ExecuteNonQuery();
        }
    }
}
