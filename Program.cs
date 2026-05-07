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

    // Словарь с твоими ссылками
    static Dictionary<string, CityImages> cityImages = new Dictionary<string, CityImages>
    {
        ["Almetyevsk"] = new CityImages
        {
            Day    = "https://ic.pics.livejournal.com/zdorovs/16627846/1742999/1742999_original.jpg",
            Evening = "https://ic.pics.livejournal.com/zdorovs/16627846/1749257/1749257_original.jpg",
            Night  = "https://photocentra.ru/images/main28/285795_main.jpg"
        },
        ["Shymkent"] = new CityImages
        {
            Day    = "https://avatars.mds.yandex.net/i?id=23b3468cd84a555f5ad7feecb4f9fbef_l-5288220-images-thumbs&n=13",
            Evening = "https://informburo.kz/storage/photos/oldArticle/main/SRhiOULsnSUvifkd.jpg",
            Night  = "https://i.ytimg.com/vi/F1PKeSICD-c/maxresdefault.jpg"
        },
        // Для любого другого города – заглушки
        ["default"] = new CityImages
        {
            Day    = "https://i.ibb.co/0jQp5vL/default-day.jpg",
            Evening = "https://i.ibb.co/v4p9v0w/default-evening.jpg",
            Night  = "https://i.ibb.co/WDfT9YK/default-night.jpg"
        }
    };

    class CityImages
    {
        public string? Day { get; set; }
        public string? Evening { get; set; }
        public string? Night { get; set; }
    }

    static async Task Main()
    {
        // Веб-сервер для Railway
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

        // Бот
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
        Console.WriteLine($"Получено обновление: {update.Type}");

        if (update.CallbackQuery != null)
        {
            await HandleCallback(botClient, update.CallbackQuery, ct);
            return;
        }

        if (update.Message?.Text == null) return;

        var chatId = update.Message.Chat.Id;
        var text = update.Message.Text.Trim();

        Console.WriteLine($"Сообщение от {chatId}: {text}");

        if (waitingForCity)
        {
            currentCity = text;
            currentCityRu = text;
            waitingForCity = false;
            await SendMainMenu(chatId, ct);
            return;
        }

        await SendMainMenu(chatId, ct);
    }

    static async Task HandleCallback(ITelegramBotClient botClient, CallbackQuery query, CancellationToken ct)
    {
        var chatId = query.Message.Chat.Id;
        var data = query.Data;

        await botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        if (data == "news")
        {
            var msg = await GetNews();
            await botClient.SendMessage(chatId, msg, cancellationToken: ct);
            await SendMainMenu(chatId, ct);
        }
        else if (data == "choose_city")
        {
            await SendCityMenu(chatId, ct);
        }
        else if (data == "city_almetyevsk")
        {
            currentCity = "Almetyevsk";
            currentCityRu = "Альметьевск";
            await SendMainMenu(chatId, ct);
        }
        else if (data == "city_shymkent")
        {
            currentCity = "Shymkent";
            currentCityRu = "Шымкент";
            await SendMainMenu(chatId, ct);
        }
        else if (data == "city_custom")
        {
            waitingForCity = true;
            await botClient.SendMessage(chatId,
                "✏️ Напиши название города на английском языке\nНапример: Moscow, London, Paris",
                cancellationToken: ct);
        }
        else if (data == "back")
        {
            await SendMainMenu(chatId, ct);
        }
    }

    static async Task SendMainMenu(long chatId, CancellationToken ct)
    {
        var (weatherText, _) = await GetWeather();
        var timeString = GetTime();
        var timeOfDay = GetTimeOfDay(); // теперь по местному времени

        var caption = $"🏙 Текущий город: *{currentCityRu}*\n" +
                      $"{timeString}\n" +
                      $"{weatherText}\n\n" +
                      $"Выберите действие:";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("📰 Новости", "news") },
            new[] { InlineKeyboardButton.WithCallbackData("🌍 Выбрать город", "choose_city") }
        });

        string? imageUrl = GetCityImageUrl(currentCity, timeOfDay);
        if (!string.IsNullOrEmpty(imageUrl))
        {
            try
            {
                await bot.SendPhoto(
                    chatId: chatId,
                    photo: InputFile.FromUri(imageUrl),
                    caption: caption,
                    parseMode: ParseMode.Markdown,
                    replyMarkup: keyboard,
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка отправки фото: {ex.Message}. Отправляем текст.");
                await bot.SendMessage(
                    chatId: chatId,
                    text: caption,
                    parseMode: ParseMode.Markdown,
                    replyMarkup: keyboard,
                    cancellationToken: ct);
            }
        }
        else
        {
            await bot.SendMessage(
                chatId: chatId,
                text: caption,
                parseMode: ParseMode.Markdown,
                replyMarkup: keyboard,
                cancellationToken: ct);
        }
    }

    static async Task SendCityMenu(long chatId, CancellationToken ct)
    {
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("🇷🇺 Альметьевск", "city_almetyevsk") },
            new[] { InlineKeyboardButton.WithCallbackData("🇰🇿 Шымкент", "city_shymkent") },
            new[] { InlineKeyboardButton.WithCallbackData("✏️ Другой город", "city_custom") },
            new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "back") }
        });

        await bot.SendMessage(chatId, "🌍 Выбери город:", replyMarkup: keyboard, cancellationToken: ct);
    }

    // ====== Вспомогательные методы для времени ======
    /// <summary>
    /// Возвращает DateTime в локальном времени выбранного города.
    /// </summary>
    static DateTime GetLocalDateTime()
    {
        string tzId;
        if (currentCity == "Shymkent")
            tzId = "West Asia Standard Time"; // UTC+5
        else
            tzId = "Russian Standard Time";    // UTC+3

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch
        {
            // fallback: предполагаем UTC+3 для Альметьевска и UTC+5 для Шымкента
            int offset = currentCity == "Shymkent" ? 5 : 3;
            return DateTime.UtcNow.AddHours(offset);
        }
    }

    /// <summary>
    /// Возвращает строку с текущим временем в городе (как раньше).
    /// </summary>
    static string GetTime()
    {
        var localTime = GetLocalDateTime();
        return $"🕐 Время в {currentCityRu}:\n{localTime:HH:mm:ss}\n📅 {localTime:dd.MM.yyyy}";
    }

    /// <summary>
    /// Определяет время суток по местному времени: "day", "evening" или "night".
    /// </summary>
    static string GetTimeOfDay()
    {
        var hour = GetLocalDateTime().Hour;
        if (hour >= 5 && hour < 12) return "evening";
        if (hour >= 12 && hour < 18) return "day"; // 12:00–17:59 считаем вечером/утром
        return "night";                                   // 18:00–4:59
    }

    static string? GetCityImageUrl(string city, string timeOfDay)
    {
        if (cityImages.ContainsKey(city))
        {
            var images = cityImages[city];
            return timeOfDay switch
            {
                "day" => images.Day,
                "evening" => images.Evening,
                "night" => images.Night,
                _ => null
            };
        }
        // fallback на default
        if (cityImages.ContainsKey("default"))
        {
            var defaultImages = cityImages["default"];
            return timeOfDay switch
            {
                "day" => defaultImages.Day,
                "evening" => defaultImages.Evening,
                "night" => defaultImages.Night,
                _ => null
            };
        }
        return null;
    }

    // ====== Погода ======
    static async Task<(string text, string? iconUrl)> GetWeather()
    {
        try
        {
            var url = $"https://api.openweathermap.org/data/2.5/weather?q={currentCity}&appid={WEATHER_KEY}&units=metric&lang=ru";
            var response = await http.GetStringAsync(url);
            var json = JObject.Parse(response);

            var temp = json["main"]["temp"];
            var feels = json["main"]["feels_like"];
            var desc = json["weather"][0]["description"];
            var humidity = json["main"]["humidity"];
            var wind = json["wind"]["speed"];
            var icon = json["weather"][0]["icon"]?.ToString();

            string? iconUrl = null;
            if (!string.IsNullOrEmpty(icon))
                iconUrl = $"https://openweathermap.org/img/wn/{icon}@2x.png";

            var text = $"🌤 Погода в {currentCityRu}:\n" +
                       $"🌡 Температура: {temp:F0}°C\n" +
                       $"🤔 Ощущается как: {feels:F0}°C\n" +
                       $"💧 Влажность: {humidity}%\n" +
                       $"💨 Ветер: {wind} м/с\n" +
                       $"☁ {desc}";

            return (text, iconUrl);
        }
        catch (Exception ex)
        {
            return ($"❌ Ошибка погоды: {ex.Message}", null);
        }
    }

    // ====== Новости ======
    static async Task<string> GetNews()
    {
        try
        {
            var query = Uri.EscapeDataString(currentCityRu);
            var url = $"https://news.google.com/rss/search?q={query}&hl=ru&gl=RU&ceid=RU:ru";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var response = await http.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            var xml = XDocument.Parse(content);
            var items = xml.Descendants("item");

            if (!items.Any())
                return $"📰 Новостей по {currentCityRu} не найдено.";

            var result = $"📰 Новости {currentCityRu}:\n\n";
            int i = 1;
            foreach (var item in items.Take(5))
            {
                var title = item.Element("title")?.Value;
                var link = item.Element("link")?.Value;
                var pubDate = item.Element("pubDate")?.Value;
                string date = "";
                if (DateTime.TryParse(pubDate, out var dt))
                    date = dt.ToString("dd.MM");
                result += $"{i}. [{date}] {title}\n{link}\n\n";
                i++;
            }
            return result;
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка новостей: {ex.Message}";
        }
    }

    static Task HandleError(ITelegramBotClient botClient, Exception ex, HandleErrorSource source, CancellationToken ct)
    {
        Console.WriteLine($"Ошибка: {ex.Message}");
        return Task.CompletedTask;
    }
}
