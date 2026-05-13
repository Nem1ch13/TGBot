using Newtonsoft.Json.Linq;
using System;
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

    static string currentCity = "Almetyevsk";
    static string currentCityRu = "Альметьевск";
    static bool waitingForCity = false;

    // ID главного меню для редактирования
    static Dictionary<long, int> mainMenuId = new Dictionary<long, int>();
    // ID доп. сообщения (новости, прогноз и т.д.) для удаления
    static Dictionary<long, int> extraMessageId = new Dictionary<long, int>();

    static async Task Main()
    {
        var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseUrls($"http://*:{port}");
                webBuilder.Configure(app =>
                {
                    app.Run(async context =>
                    {
                        await context.Response.WriteAsync("Bot is running");
                    });
                });
            })
            .Build();

        _ = host.RunAsync();

        bot = new TelegramBotClient(TG_TOKEN);
        var cts = new CancellationTokenSource();
        bot.StartReceiving(
            HandleUpdate,
            HandleError,
            new ReceiverOptions { AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery } },
            cts.Token
        );

        var me = await bot.GetMe();
        Console.WriteLine($"Бот @{me.Username} запущен!");
        await Task.Delay(Timeout.Infinite, cts.Token);
    }

    static async Task HandleUpdate(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        if (update.CallbackQuery != null)
        {
            await HandleCallback(botClient, update.CallbackQuery, ct);
            return;
        }

        if (update.Message?.Text == null) return;

        var chatId = update.Message.Chat.Id;
        var text = update.Message.Text.Trim();

        // Удаляем сообщение пользователя
        try { await bot.DeleteMessage(chatId, update.Message.MessageId, ct); } catch { }

        if (waitingForCity)
        {
            waitingForCity = false;
            currentCity = text;
            currentCityRu = text;
            // Удаляем подсказку "напиши город"
            await DeleteExtra(chatId, ct);
        }

        await ShowMainMenu(chatId, ct);
    }

    static async Task HandleCallback(ITelegramBotClient botClient, CallbackQuery query, CancellationToken ct)
    {
        var chatId = query.Message.Chat.Id;
        var data = query.Data;

        await botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        switch (data)
        {
            case "back":
                await DeleteExtra(chatId, ct);
                await ShowMainMenu(chatId, ct);
                break;

            case "news":
            case "forecast":
            case "rates":
                string msgText = data switch
                {
                    "news" => await GetNews(),
                    "forecast" => await GetForecast(),
                    "rates" => await GetRates(),
                    _ => ""
                };
                await DeleteExtra(chatId, ct);
                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
                });
                var sent = await bot.SendMessage(chatId, msgText,
                    replyMarkup: keyboard, cancellationToken: ct);
                extraMessageId[chatId] = sent.MessageId;
                break;

            case "choose_city":
                await DeleteExtra(chatId, ct);
                await SendCityMenu(chatId, ct);
                break;

            case "city_almetyevsk": SetCity("Almetyevsk", "Альметьевск"); goto updateMenu;
            case "city_shymkent":   SetCity("Shymkent", "Шымкент");       goto updateMenu;
            case "city_moscow":     SetCity("Moscow", "Москва");          goto updateMenu;
            case "city_spb":        SetCity("Saint Petersburg", "Санкт-Петербург"); goto updateMenu;
            case "city_kazan":      SetCity("Kazan", "Казань");           goto updateMenu;
            case "city_istanbul":   SetCity("Istanbul", "Стамбул");       goto updateMenu;
            case "city_almaty":     SetCity("Almaty", "Алматы");          goto updateMenu;
            case "city_dubai":      SetCity("Dubai", "Дубай");            goto updateMenu;
            case "city_london":     SetCity("London", "Лондон");          goto updateMenu;
            case "city_custom":
                waitingForCity = true;
                await DeleteExtra(chatId, ct);
                var promptSent = await bot.SendMessage(chatId,
                    "✏️ Напиши название города на английском языке\nНапример: Moscow, London, Paris",
                    cancellationToken: ct);
                extraMessageId[chatId] = promptSent.MessageId;
                return;

            updateMenu:
                await DeleteExtra(chatId, ct);
                await ShowMainMenu(chatId, ct);
                break;
        }
    }

    static void SetCity(string eng, string ru)
    {
        currentCity = eng;
        currentCityRu = ru;
    }

    static async Task DeleteExtra(long chatId, CancellationToken ct)
    {
        if (extraMessageId.TryGetValue(chatId, out var msgId))
        {
            try { await bot.DeleteMessage(chatId, msgId, ct); } catch { }
            extraMessageId.Remove(chatId);
        }
    }

    // Главное меню — редактируем если уже есть, иначе создаём новое
    static async Task ShowMainMenu(long chatId, CancellationToken ct)
    {
        var (weatherText, _) = await GetWeather();
        var timeString = GetTime();

        var text = $"🏙 *{currentCityRu}*\n{timeString}\n\n{weatherText}\n\nВыберите действие:";
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] {
                InlineKeyboardButton.WithCallbackData("📰 Новости", "news"),
                InlineKeyboardButton.WithCallbackData("📆 Прогноз", "forecast")
            },
            new[] {
                InlineKeyboardButton.WithCallbackData("💵 Курсы валют", "rates"),
                InlineKeyboardButton.WithCallbackData("🌍 Сменить город", "choose_city")
            }
        });

        if (mainMenuId.TryGetValue(chatId, out var existingId))
        {
            try
            {
                await bot.EditMessageText(chatId, existingId, text,
                    parseMode: ParseMode.Markdown,
                    replyMarkup: keyboard,
                    cancellationToken: ct);
                return;
            }
            catch { /* сообщение устарело — отправим новое */ }
        }

        var sent = await bot.SendMessage(chatId, text,
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: ct);
        mainMenuId[chatId] = sent.MessageId;
    }

    static async Task SendCityMenu(long chatId, CancellationToken ct)
    {
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] {
                InlineKeyboardButton.WithCallbackData("🇷🇺 Альметьевск", "city_almetyevsk"),
                InlineKeyboardButton.WithCallbackData("🇰🇿 Шымкент", "city_shymkent")
            },
            new[] {
                InlineKeyboardButton.WithCallbackData("🇷🇺 Москва", "city_moscow"),
                InlineKeyboardButton.WithCallbackData("🇷🇺 Казань", "city_kazan")
            },
            new[] {
                InlineKeyboardButton.WithCallbackData("🇷🇺 Санкт-Петербург", "city_spb"),
                InlineKeyboardButton.WithCallbackData("🇰🇿 Алматы", "city_almaty")
            },
            new[] {
                InlineKeyboardButton.WithCallbackData("🇹🇷 Стамбул", "city_istanbul"),
                InlineKeyboardButton.WithCallbackData("🇦🇪 Дубай", "city_dubai")
            },
            new[] {
                InlineKeyboardButton.WithCallbackData("🇬🇧 Лондон", "city_london"),
                InlineKeyboardButton.WithCallbackData("✏️ Другой", "city_custom")
            },
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });

        var sent = await bot.SendMessage(chatId, "🌍 Выбери город:",
            replyMarkup: keyboard, cancellationToken: ct);
        extraMessageId[chatId] = sent.MessageId;
    }

    static DateTime GetLocalDateTime()
    {
        string tzId = currentCity switch
        {
            "Shymkent" or "Almaty" => "West Asia Standard Time",
            "Moscow" or "Saint Petersburg" or "Kazan" => "Russian Standard Time",
            "Istanbul" or "Dubai" => "Arabian Standard Time",
            "London" => "GMT Standard Time",
            _ => "Russian Standard Time"
        };
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch
        {
            return DateTime.UtcNow.AddHours(3);
        }
    }

    static string GetTime()
    {
        var t = GetLocalDateTime();
        return $"🕐 {t:HH:mm} | 📅 {t:dd.MM.yyyy}";
    }

    static async Task<(string text, string? iconUrl)> GetWeather()
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/weather?q={currentCity}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));

            var temp   = json["main"]["temp"];
            var feels  = json["main"]["feels_like"];
            var desc   = json["weather"][0]["description"];
            var hum    = json["main"]["humidity"];
            var wind   = json["wind"]["speed"];

            var text = $"🌤 *Погода:* {temp:F0}°C (ощущается {feels:F0}°C)\n" +
                       $"☁ {desc} | 💧{hum}% | 💨{wind} м/с";
            return (text, null);
        }
        catch (Exception ex)
        {
            return ($"❌ Погода: {ex.Message}", null);
        }
    }

    static async Task<string> GetForecast()
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/forecast?q={currentCity}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var json = JObject.Parse(await http.GetStringAsync(url));
            var list = json["list"] as JArray;
            if (list == null) return "Прогноз не найден.";

            var daily = new Dictionary<string, (double min, double max, string desc)>();
            foreach (var item in list)
            {
                var dt   = DateTime.Parse(item["dt_txt"].ToString());
                var day  = dt.ToString("dd.MM (ddd)");
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

            var result = $"📆 Прогноз на 5 дней — {currentCityRu}:\n\n";
            foreach (var d in daily.Take(5))
                result += $"📅 {d.Key}: {d.Value.min:F0}°…{d.Value.max:F0}°, {d.Value.desc}\n";
            return result;
        }
        catch (Exception ex) { return $"❌ Ошибка прогноза: {ex.Message}"; }
    }

    static async Task<string> GetNews()
    {
        try
        {
            var query = Uri.EscapeDataString(currentCityRu);
            var url = $"https://news.google.com/rss/search?q={query}&hl=ru&gl=RU&ceid=RU:ru";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0");
            var content = await (await http.SendAsync(request)).Content.ReadAsStringAsync();
            var items = XDocument.Parse(content).Descendants("item");

            if (!items.Any()) return $"📰 Новостей по {currentCityRu} не найдено.";

            var result = $"📰 Новости {currentCityRu}:\n\n";
            int i = 1;
            foreach (var item in items.Take(5))
            {
                var title = item.Element("title")?.Value;
                var link  = item.Element("link")?.Value;
                var date  = DateTime.TryParse(item.Element("pubDate")?.Value, out var dt)
                    ? dt.ToString("dd.MM") : "";
                result += $"{i}. [{date}] {title}\n{link}\n\n";
                i++;
            }
            return result;
        }
        catch (Exception ex) { return $"❌ Ошибка новостей: {ex.Message}"; }
    }

    static async Task<string> GetRates()
    {
        try
        {
            var json = JObject.Parse(await http.GetStringAsync("https://api.exchangerate-api.com/v4/latest/USD"));
            var r = json["rates"];

            decimal Get(string code) => r[code]?.Value<decimal>() ?? 0;

            var usd = 1m;
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

    static Task HandleError(ITelegramBotClient botClient, Exception ex, HandleErrorSource source, CancellationToken ct)
    {
        Console.WriteLine($"Ошибка: {ex.Message}");
        return Task.CompletedTask;
    }
}
