using Newtonsoft.Json.Linq;
using System;
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
using Microsoft.Extensions.DependencyInjection;
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

    static async Task Main()
    {
        // ===== Healthcheck-сервер =====
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

        // ===== Запуск бота =====
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

        await Task.Delay(Timeout.Infinite); // держим приложение живым
    }

    // Обработчик сообщений
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
            await botClient.SendMessage(chatId, $"✅ Выбран город: {text}", cancellationToken: ct);
            await SendMainMenu(chatId, ct);
            return;
        }

        await SendMainMenu(chatId, ct);
    }

    // Остальные методы (HandleCallback, SendMainMenu, SendCityMenu, GetWeather, GetTime, GetNews, HandleError)
    // оставь без изменений, как в исходном коде.
}
