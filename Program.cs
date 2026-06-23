using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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

class Program
{
    static string TG_TOKEN = Environment.GetEnvironmentVariable("TG_TOKEN")!;
    static string WEATHER_KEY = Environment.GetEnvironmentVariable("WEATHER_KEY")!;

    static TelegramBotClient bot = null!;
    static HttpClient http = new HttpClient();

    // === ИЗМЕНЕНИЕ: добавлен злотый ===
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
        ["PLN"] = "🇵🇱 Злотый"   // <-- Добавлено
    };

    static readonly string[] WorldCitiesEn = { "London", "Tokyo", "Berlin", "Paris", "Rome", "Madrid", "Beijing", "Sydney", "New York", "Toronto", "Seoul", "Bangkok", "Dubai", "Singapore", "Mumbai" };
    static readonly Random rng = new();

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

        public List<string> Favorites = new();
        public List<string> ConversionHistory = new();
        public string? RemindTime;
        public DateTime LastNotifyDate;
    }

    static ConcurrentDictionary<long, UserState> users = new();
    static ConcurrentDictionary<long, int> subscribers = new();

    static async Task Main()
    {
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

        await Task.Delay(Timeout.Infinite, cts.Token);
    }

    // ... остальные методы HandleUpdate, HandleLocation, HandleCallback остаются без изменений ...

    // === ИЗМЕНЕНИЕ: Утренняя рассылка с датой, дневным прогнозом и без звездочек ===
    static async Task DailyNotifyLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in subscribers)
            {
                var chatId = kv.Key;
                if (!users.TryGetValue(chatId, out var user)) continue;
                var tz = GetTimeZone(user.CityEn);
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, tz);
                var targetTime = user.RemindTime ?? "07:00";
                if (localNow.ToString("HH:mm") == targetTime && localNow.Date != user.LastNotifyDate)
                {
                    user.LastNotifyDate = localNow.Date;

                    // Получаем текущую погоду (без маркдауна)
                    var (currentWeather, _) = await GetWeather(user.CityEn, user.CityRu, useMarkdown: false);

                    // Получаем прогноз на сегодня (мин/макс)
                    var todayForecast = await GetTodayForecast(user.CityEn, user.CityRu);

                    var message = $"🌅 Доброе утро! Погода в {user.CityRu} на {localNow:dd.MM.yyyy}:\n\n" +
                                  $"{currentWeather}\n" +
                                  $"{todayForecast}";

                    // Отправляем без ParseMode, чтобы не было жирного текста
                    try { await bot.SendMessage(chatId, message, cancellationToken: ct); } catch { }
                }
            }
            await Task.Delay(30_000, ct);
        }
    }

    // === ИЗМЕНЕНИЕ: Добавлен параметр useMarkdown в GetWeather ===
    static async Task<(string text, double? temp)> GetWeather(string cityEn, string cityRu, bool useMarkdown = true)
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/weather?q={cityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            double temp = json["main"]!["temp"]!.Value<double>();
            double feels = json["main"]!["feels_like"]!.Value<double>();
            string desc = json["weather"]![0]!["description"]!.ToString();
            int hum = json["main"]!["humidity"]!.Value<int>();
            double wind = json["wind"]!["speed"]!.Value<double>();
            string iconCode = json["weather"]![0]!["icon"]?.ToString() ?? "01d";

            string emoji = iconCode switch
            {
                "01d" => "☀️", "01n" => "🌙",
                "02d" => "⛅", "02n" => "🌙",
                "03d" or "03n" => "☁",
                "04d" or "04n" => "☁",
                "09d" or "09n" => "🌧",
                "10d" => "🌦", "10n" => "🌧",
                "11d" or "11n" => "⛈",
                "13d" or "13n" => "🌨",
                "50d" or "50n" => "🌫",
                _ => "🌡"
            };

            // Формируем текст: если useMarkdown, то выделяем жирным, иначе без звездочек
            string weatherLine = useMarkdown
                ? $"🌤 *Погода:* {emoji} {temp:F0}°C (ощ. {feels:F0}°C)"
                : $"🌤 Погода: {emoji} {temp:F0}°C (ощущается {feels:F0}°C)";

            string text = $"{weatherLine}\n☁ {desc} | 💧{hum}% | 💨{wind} м/с";
            return (text, temp);
        }
        catch (Exception ex) { return ($"❌ Погода: {ex.Message}", null); }
    }

    // === ИЗМЕНЕНИЕ: Новый метод для получения прогноза на сегодня (мин/макс) ===
    static async Task<string> GetTodayForecast(string cityEn, string cityRu)
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/forecast?q={cityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            var list = json["list"] as JArray;
            if (list == null) return "";

            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var todayItems = list.Where(i => i["dt_txt"]!.ToString().StartsWith(today)).ToList();
            if (!todayItems.Any()) return "📆 Прогноз на сегодня отсутствует.";

            double min = double.MaxValue, max = double.MinValue;
            string mainDesc = "";
            foreach (var item in todayItems)
            {
                double t = item["main"]!["temp"]!.Value<double>();
                if (t < min) min = t;
                if (t > max) max = t;
                // Берём описание последнего (или первого) периода
                mainDesc = item["weather"]![0]!["description"]!.ToString();
            }
            return $"📆 Сегодня: от {min:F0}°C до {max:F0}°C, {mainDesc}";
        }
        catch { return ""; }
    }

    // === ИЗМЕНЕНИЕ: В таблицу курсов добавлен злотый ===
    static async Task<string> GetRates()
    {
        try
        {
            var json = JObject.Parse(await http.GetStringAsync("https://api.exchangerate-api.com/v4/latest/USD"));
            var r = json["rates"]!;
            decimal Get(string code) => r[code]?.Value<decimal>() ?? 0;
            decimal rub = Get("RUB"), kzt = Get("KZT"), eur = Get("EUR"), gbp = Get("GBP"),
                    cny = Get("CNY"), aed = Get("AED"), try_ = Get("TRY"), uah = Get("UAH"),
                    kgs = Get("KGS"), uzs = Get("UZS"), pln = Get("PLN");  // добавлено
            decimal Safe(decimal a, decimal b) => b == 0 ? 0 : a / b;

            return "<pre>" +
                   "Валюта      │ USD        │ RUB        │ KZT        \n" +
                   "────────────┼────────────┼────────────┼────────────\n" +
                   $"🇷🇺 RUB      │ {1/rub,10:F4} │     1.0000 │ {1/(rub/kzt),10:F2}\n" +
                   $"🇰🇿 KZT      │ {1/kzt,10:F4} │ {Safe(1, rub/kzt),10:F4} │     1.0000\n" +
                   $"🇺🇸 USD      │     1.0000 │ {rub,10:F2} │ {kzt,10:F2}\n" +
                   $"🇪🇺 EUR      │ {eur,10:F4} │ {Safe(rub, eur),10:F2} │ {Safe(kzt, eur),10:F2}\n" +
                   $"🇬🇧 GBP      │ {gbp,10:F4} │ {Safe(rub, gbp),10:F2} │ {Safe(kzt, gbp),10:F2}\n" +
                   $"🇨🇳 CNY      │ {cny,10:F4} │ {Safe(rub, cny),10:F2} │ {Safe(kzt, cny),10:F2}\n" +
                   $"🇦🇪 AED      │ {aed,10:F4} │ {Safe(rub, aed),10:F2} │ {Safe(kzt, aed),10:F2}\n" +
                   $"🇹🇷 TRY      │ {try_,10:F4} │ {Safe(rub, try_),10:F2} │ {Safe(kzt, try_),10:F2}\n" +
                   $"🇺🇦 UAH      │ {uah,10:F4} │ {Safe(rub, uah),10:F2} │ {Safe(kzt, uah),10:F2}\n" +
                   $"🇰🇬 KGS      │ {kgs,10:F4} │ {Safe(rub, kgs),10:F2} │ {Safe(kzt, kgs),10:F2}\n" +
                   $"🇺🇿 UZS      │ {uzs,10:F0} │ {Safe(rub, uzs),10:F0} │ {Safe(kzt, uzs),10:F0}\n" +
                   $"🇵🇱 PLN      │ {1/pln,10:F4} │ {Safe(rub, pln),10:F2} │ {Safe(kzt, pln),10:F2}\n" +  // добавлено
                   "</pre>";
        }
        catch (Exception ex) { return $"❌ Ошибка курсов: {ex.Message}"; }
    }

    // === ИЗМЕНЕНИЕ: В конвертере добавлен злотый в массив валют ===
    static async Task EditConvertFromMenu(long chatId, UserState user, CancellationToken ct)
    {
        var currencies = new[] { "RUB", "KZT", "USD", "EUR", "GBP", "PLN", "CNY", "AED", "TRY", "UAH", "KGS", "UZS" }; // добавлено PLN
        var buttons = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < currencies.Length; i += 3)
            buttons.Add(currencies.Skip(i).Take(3).Select(c => InlineKeyboardButton.WithCallbackData(CurrencyName(c), $"convfrom_{c}")).ToArray());
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("📋 История", "history"), InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") });
        await EditOrSendMessage(chatId, user, "💱 Выберите исходную валюту:", new InlineKeyboardMarkup(buttons), ct);
    }

    static async Task EditConvertToMenu(long chatId, UserState user, CancellationToken ct)
    {
        var currencies = new[] { "RUB", "KZT", "USD", "EUR", "GBP", "PLN", "CNY", "AED", "TRY", "UAH", "KGS", "UZS" }; // добавлено PLN
        var buttons = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < currencies.Length; i += 3)
            buttons.Add(currencies.Skip(i).Take(3).Select(c => InlineKeyboardButton.WithCallbackData(CurrencyName(c), $"convto_{c}")).ToArray());
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") });
        await EditOrSendMessage(chatId, user, $"💱 {CurrencyName(user.ConvertFrom!)} → ?\nВыберите целевую валюту:", new InlineKeyboardMarkup(buttons), ct);
    }

    // ... остальные методы остаются без изменений (EditMainMenu, EditForecast, EditToday, EditNews, EditRates, EditHelp, EditMapLink, EditCityMenu, EditOrSendMessage, EditOrSendMain, TryGetWeather, GetForecast, HandleInlineQuery, AnimateLoading, GetTimeZone, GetUtcOffset, GetLocalTimeString, HandleError, HandleAmountInput, EditHistory) ...

    // Не забудь оставить остальные методы, которые я не показал для краткости. В твоем исходном коде они есть.
}
