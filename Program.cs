using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    static string TG_TOKEN = Environment.GetEnvironmentVariable("TG_TOKEN");
    static string WEATHER_KEY = Environment.GetEnvironmentVariable("WEATHER_KEY");

    static TelegramBotClient bot;
    static HttpClient http = new HttpClient();

    class UserState
    {
        public string CityEn = "Almetyevsk";
        public string CityRu = "Альметьевск";
        public bool WaitingForCustomCity;
        public bool WaitingForConversion;
        public bool Subscribed;
        public int MainMessageId;
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

    static async Task HandleUpdate(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        if (update.InlineQuery != null)
        {
            await HandleInlineQuery(botClient, update.InlineQuery, ct);
            return;
        }

        if (update.CallbackQuery != null)
        {
            await HandleCallback(botClient, update.CallbackQuery, ct);
            return;
        }

        if (update.Message?.Text == null) return;

        var chatId = update.Message.Chat.Id;
        var text = update.Message.Text.Trim();
        var user = users.GetOrAdd(chatId, _ => new UserState());

        try { await bot.DeleteMessage(chatId, update.Message.MessageId, ct); } catch { }

        if (user.WaitingForCustomCity)
        {
            user.WaitingForCustomCity = false;
            var (ok, _) = await TryGetWeather(text);
            if (!ok)
            {
                await EditOrSendMain(chatId, user, ct, extra: $"❌ Город \"{text}\" не найден.");
                return;
            }
            user.CityEn = text;
            user.CityRu = text;
            await EditMainMenu(chatId, user, ct);
            return;
        }

        if (user.WaitingForConversion)
        {
            user.WaitingForConversion = false;
            await HandleConversionInput(chatId, user, text, ct);
            return;
        }

        await EditMainMenu(chatId, user, ct);
    }

    static async Task HandleCallback(ITelegramBotClient botClient, CallbackQuery query, CancellationToken ct)
    {
        var chatId = query.Message.Chat.Id;
        var data = query.Data;
        var user = users.GetOrAdd(chatId, _ => new UserState());
        user.MainMessageId = query.Message.MessageId;

        await botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        switch (data)
        {
            case "refresh":
            case "back":
                await EditMainMenu(chatId, user, ct);
                break;

            case "news":
                await EditNews(chatId, user, ct);
                break;
            case "forecast":
                await EditForecast(chatId, user, ct);
                break;
            case "rates":
                await EditRates(chatId, user, ct);
                break;
            case "convert":
                user.WaitingForConversion = true;
                await EditOrSendMain(chatId, user, ct,
                    extra: "🧮 Введите сумму и валюты, например:\n`100 USD RUB`\n(поддерживаются RUB, KZT, USD, EUR, GBP, CNY, AED, TRY, UAH, KGS, UZS)",
                    keyboard: new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("◀️ Отмена", "back") }
                    }));
                break;

            case "subscribe":
                user.Subscribed = !user.Subscribed;
                if (user.Subscribed)
                    subscribers[chatId] = GetUtcOffset(user.CityEn);
                else
                    subscribers.TryRemove(chatId, out _);
                await EditMainMenu(chatId, user, ct);
                break;

            case "city_almetyevsk": SetCity(user, "Almetyevsk", "Альметьевск"); await EditMainMenu(chatId, user, ct); break;
            case "city_shymkent":   SetCity(user, "Shymkent", "Шымкент");     await EditMainMenu(chatId, user, ct); break;
            case "city_moscow":     SetCity(user, "Moscow", "Москва");         await EditMainMenu(chatId, user, ct); break;
            case "city_almaty":     SetCity(user, "Almaty", "Алматы");         await EditMainMenu(chatId, user, ct); break;
            case "city_astana":     SetCity(user, "Astana", "Астана");         await EditMainMenu(chatId, user, ct); break;

            case "city_custom":
                user.WaitingForCustomCity = true;
                await EditOrSendMain(chatId, user, ct,
                    extra: "✏️ Введите название города на английском:",
                    keyboard: new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("◀️ Отмена", "back") }
                    }));
                break;

            case "choose_city":
                await EditCityMenu(chatId, user, ct);
                break;
        }
    }

    static void SetCity(UserState user, string en, string ru)
    {
        user.CityEn = en;
        user.CityRu = ru;
    }

    // ========== Главное меню и редактирование ==========
    static async Task EditMainMenu(long chatId, UserState user, CancellationToken ct)
    {
        var (weatherText, _) = await GetWeather(user.CityEn, user.CityRu);
        var timeStr = GetLocalTimeString(user.CityEn, user.CityRu);
        var text = $"🏙 *{user.CityRu}*\n{timeStr}\n\n{weatherText}\n\nВыберите действие:";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("📰 Новости", "news"),
                    InlineKeyboardButton.WithCallbackData("📆 Прогноз", "forecast") },
            new[] { InlineKeyboardButton.WithCallbackData("💵 Курсы", "rates"),
                    InlineKeyboardButton.WithCallbackData("🧮 Конвертер", "convert") },
            new[] { InlineKeyboardButton.WithCallbackData("🌍 Город", "choose_city"),
                    InlineKeyboardButton.WithCallbackData(user.Subscribed ? "🔕 Отписаться" : "🔔 Подписаться", "subscribe") }
        });

        await EditOrSendMessage(chatId, user, text, keyboard, ct);
    }

    static async Task EditNews(long chatId, UserState user, CancellationToken ct)
    {
        var news = await GetNews(user.CityRu);
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });
        await EditOrSendMessage(chatId, user, news, keyboard, ct);
    }

    static async Task EditForecast(long chatId, UserState user, CancellationToken ct)
    {
        var forecast = await GetForecast(user.CityEn, user.CityRu);
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });
        await EditOrSendMessage(chatId, user, forecast, keyboard, ct);
    }

    static async Task EditRates(long chatId, UserState user, CancellationToken ct)
    {
        var rates = await GetRates();
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });
        await EditOrSendMessage(chatId, user, rates, keyboard, ct);
    }

    static async Task EditCityMenu(long chatId, UserState user, CancellationToken ct)
    {
        var text = $"🌍 Текущий город: *{user.CityRu}*\nВыберите новый:";
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("🇷🇺 Альметьевск", "city_almetyevsk"),
                    InlineKeyboardButton.WithCallbackData("🇰🇿 Шымкент", "city_shymkent") },
            new[] { InlineKeyboardButton.WithCallbackData("🇷🇺 Москва", "city_moscow"),
                    InlineKeyboardButton.WithCallbackData("🇰🇿 Алматы", "city_almaty") },
            new[] { InlineKeyboardButton.WithCallbackData("🇰🇿 Астана", "city_astana") },
            new[] { InlineKeyboardButton.WithCallbackData("✏️ Другой", "city_custom"),
                    InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });
        await EditOrSendMessage(chatId, user, text, keyboard, ct);
    }

    static async Task EditOrSendMessage(long chatId, UserState user, string text, InlineKeyboardMarkup keyboard, CancellationToken ct)
    {
        if (user.MainMessageId != 0)
        {
            try
            {
                await bot.EditMessageText(chatId, user.MainMessageId, text,
                    parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: ct);
                return;
            }
            catch { }
        }
        var msg = await bot.SendMessage(chatId, text,
            parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: ct);
        user.MainMessageId = msg.MessageId;
    }

    static async Task EditOrSendMain(long chatId, UserState user, CancellationToken ct, string extra = "", InlineKeyboardMarkup? keyboard = null)
    {
        var (weatherText, _) = await GetWeather(user.CityEn, user.CityRu);
        var timeStr = GetLocalTimeString(user.CityEn, user.CityRu);
        var text = $"🏙 *{user.CityRu}*\n{timeStr}\n\n{weatherText}";
        if (!string.IsNullOrEmpty(extra)) text += "\n\n" + extra;
        keyboard ??= new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });
        await EditOrSendMessage(chatId, user, text, keyboard, ct);
    }

    // ========== Конвертер валют ==========
    static async Task HandleConversionInput(long chatId, UserState user, string input, CancellationToken ct)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !decimal.TryParse(parts[0], out var amount))
        {
            await EditOrSendMain(chatId, user, ct, extra: "❌ Неверный формат. Пример: `100 USD RUB`");
            return;
        }

        var from = parts[1].ToUpper();
        var to = parts[2].ToUpper();
        try
        {
            var json = JObject.Parse(await http.GetStringAsync($"https://api.exchangerate-api.com/v4/latest/{from}"));
            var rate = json["rates"][to]?.Value<decimal>();
            if (rate == null)
            {
                await EditOrSendMain(chatId, user, ct, extra: "❌ Неподдерживаемая валюта.");
                return;
            }
            var result = amount * rate.Value;
            var text = $"🧮 {amount} {from} = {result:F2} {to}";
            await EditOrSendMain(chatId, user, ct, extra: text);
        }
        catch
        {
            await EditOrSendMain(chatId, user, ct, extra: "❌ Ошибка при получении курса.");
        }
    }

    // ========== Погода ==========
    static async Task<(bool ok, string text)> TryGetWeather(string cityEn)
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/weather?q={cityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            if (json["cod"]?.Value<int>() != 200) return (false, "");
            return (true, "");
        }
        catch { return (false, ""); }
    }

    static async Task<(string text, string? icon)> GetWeather(string cityEn, string cityRu)
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/weather?q={cityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            var temp = json["main"]["temp"];
            var feels = json["main"]["feels_like"];
            var desc = json["weather"][0]["description"];
            var hum = json["main"]["humidity"];
            var wind = json["wind"]["speed"];
            return ($"🌤 *Погода:* {temp:F0}°C (ощ. {feels:F0}°C)\n☁ {desc} | 💧{hum}% | 💨{wind} м/с", null);
        }
        catch (Exception ex) { return ($"❌ Погода: {ex.Message}", null); }
    }

    static async Task<string> GetForecast(string cityEn, string cityRu)
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/forecast?q={cityEn}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            var list = json["list"] as JArray;
            if (list == null) return "Прогноз не найден.";

            var daily = new Dictionary<string, (double min, double max, string desc)>();
            foreach (var item in list)
            {
                var dt = DateTime.Parse(item["dt_txt"].ToString());
                var day = dt.ToString("dd.MM (ddd)");
                var temp = item["main"]["temp"].Value<double>();
                var desc = item["weather"][0]["description"].ToString();
                if (!daily.ContainsKey(day))
                    daily[day] = (temp, temp, desc);
                else
                {
                    var cur = daily[day];
                    daily[day] = (Math.Min(cur.min, temp), Math.Max(cur.max, temp), cur.desc);
                }
            }

            var result = $"📆 Прогноз на 5 дней — {cityRu}:\n\n";
            foreach (var d in daily.Take(5))
                result += $"📅 {d.Key}: {d.Value.min:F0}°…{d.Value.max:F0}°, {d.Value.desc}\n";
            return result;
        }
        catch (Exception ex) { return $"❌ Ошибка прогноза: {ex.Message}"; }
    }

    // ========== Новости ==========
    static async Task<string> GetNews(string cityRu)
    {
        try
        {
            var query = Uri.EscapeDataString(cityRu);
            var url = $"https://news.google.com/rss/search?q={query}&hl=ru&gl=RU&ceid=RU:ru";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0");
            var content = await (await http.SendAsync(request)).Content.ReadAsStringAsync();
            var items = XDocument.Parse(content).Descendants("item");

            if (!items.Any()) return $"📰 Новостей по {cityRu} не найдено.";

            var result = $"📰 Новости {cityRu}:\n\n";
            int i = 1;
            foreach (var item in items.Take(5))
            {
                var title = item.Element("title")?.Value;
                var link = item.Element("link")?.Value;
                var date = DateTime.TryParse(item.Element("pubDate")?.Value, out var dt)
                    ? dt.ToString("dd.MM") : "";
                result += $"{i}. [{date}] {title}\n{link}\n\n";
                i++;
            }
            return result;
        }
        catch (Exception ex) { return $"❌ Ошибка новостей: {ex.Message}"; }
    }

    // ========== Курсы валют ==========
    static async Task<string> GetRates()
    {
        try
        {
            var json = JObject.Parse(await http.GetStringAsync("https://api.exchangerate-api.com/v4/latest/USD"));
            var r = json["rates"];

            decimal Get(string code) => r[code]?.Value<decimal>() ?? 0;

            var rub = Get("RUB");
            var kzt = Get("KZT");
            var eur = Get("EUR");
            var gbp = Get("GBP");
            var cny = Get("CNY");
            var aed = Get("AED");
            var try_ = Get("TRY");
            var uah = Get("UAH");
            var kgs = Get("KGS");
            var uzs = Get("UZS");

            decimal Safe(decimal a, decimal b) => b == 0 ? 0 : a / b;

            return $"💵 Курсы валют (к USD):\n\n" +
                   $"🇷🇺 1 $ = {rub:F2} ₽\n" +
                   $"🇰🇿 1 $ = {kzt:F2} ₸\n" +
                   $"🇪🇺 1 $ = {eur:F4} €  |  1 € = {Safe(rub, eur):F2} ₽\n" +
                   $"🇬🇧 1 $ = {gbp:F4} £  |  1 £ = {Safe(rub, gbp):F2} ₽\n" +
                   $"🇨🇳 1 $ = {cny:F2} ¥\n" +
                   $"🇦🇪 1 $ = {aed:F2} د.إ\n" +
                   $"🇹🇷 1 $ = {try_:F2} ₺\n" +
                   $"🇺🇦 1 $ = {uah:F2} ₴\n" +
                   $"🇰🇬 1 $ = {kgs:F2} с\n" +
                   $"🇺🇿 1 $ = {uzs:F0} сум\n\n" +
                   $"🔁 1 ₽ = {Safe(kzt, rub):F2} ₸\n" +
                   $"🔁 1 ₸ = {Safe(rub, kzt):F4} ₽";
        }
        catch (Exception ex) { return $"❌ Ошибка курсов: {ex.Message}"; }
    }

    // ========== Часовые пояса ==========
    static TimeZoneInfo GetTimeZone(string cityEn)
    {
        string id = cityEn switch
        {
            "Shymkent" or "Almaty" or "Astana" => "West Asia Standard Time",
            "Moscow" => "Russian Standard Time",
            _ => "Russian Standard Time"  // Альметьевск и всё остальное
        };
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time"); }
    }

    static int GetUtcOffset(string cityEn) => (int)GetTimeZone(cityEn).BaseUtcOffset.TotalHours;

    static string GetLocalTimeString(string cityEn, string cityRu)
    {
        var tz = GetTimeZone(cityEn);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        return $"🕐 {local:HH:mm} | 📅 {local:dd.MM.yyyy}";
    }

    // ========== Инлайн-режим ==========
    static async Task HandleInlineQuery(ITelegramBotClient botClient, InlineQuery query, CancellationToken ct)
    {
        var search = query.Query?.Trim();
        if (string.IsNullOrEmpty(search)) return;

        var (weather, _) = await GetWeather(search, search);
        var desc = weather.StartsWith("❌") ? "Город не найден" : weather;

        var result = new InlineQueryResultArticle(
            id: "1",
            title: $"Погода в {search}",
            inputMessageContent: new InputTextMessageContent(desc)
            {
                ParseMode = ParseMode.Markdown
            }
        );
        await botClient.AnswerInlineQuery(query.Id, new[] { result }, cacheTime: 10, cancellationToken: ct);
    }

    // ========== Ежедневная рассылка ==========
    static async Task DailyNotifyLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            // проверяем каждые 30 секунд, чтобы не нагружать
            foreach (var kv in subscribers)
            {
                var chatId = kv.Key;
                var user = users.GetOrAdd(chatId, _ => new UserState());
                var tz = GetTimeZone(user.CityEn);
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, tz);
                // Отправляем в 7 утра по местному времени
                if (localNow.Hour == 7 && localNow.Minute == 0)
                {
                    var (weather, _) = await GetWeather(user.CityEn, user.CityRu);
                    var time = localNow.ToString("HH:mm");
                    var msg = $"🌅 Доброе утро! Погода в {user.CityRu} на 7:00:\n{weather}";
                    try { await bot.SendMessage(chatId, msg, cancellationToken: ct); } catch { }
                }
            }
            await Task.Delay(30_000, ct);
        }
    }

    static Task HandleError(ITelegramBotClient botClient, Exception ex, HandleErrorSource source, CancellationToken ct)
    {
        Console.WriteLine($"Ошибка: {ex.Message}");
        return Task.CompletedTask;
    }
}
