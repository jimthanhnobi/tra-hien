using System.Text;
using TeaPremium.Application.Iam;
using TeaPremium.Application.UseCases;

namespace TeaPremium.Api.Iam;

public static class BootstrapOwnerConsole
{
    // Only invoked by the explicit command branch in Program. Never a hosted service.
    public static async Task<int> RunAsync(IServiceProvider services)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("bootstrap-owner yêu cầu terminal riêng tư, không nhận mật khẩu qua argument/pipe.");
            return 2;
        }
        Console.Write("Owner email: ");
        var email = Console.ReadLine() ?? "";
        Console.Write("Owner password (hidden): ");
        var password = ReadHidden();
        Console.Write("Confirm password (hidden): ");
        var confirmation = ReadHidden();
        if (password != confirmation) return 2;
        await using var scope = services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<BootstrapOwner, bool>>();
        var result = await handler.Handle(new(email, password), CancellationToken.None);
        // Never print errors returned by Identity or the input values.
        Console.WriteLine(result.IsSuccess ? result.Value ? "Owner created; complete TOTP onboarding over HTTPS." : "Owner already exists; no changes."
            : "Owner not created; validate input and database readiness.");
        return result.IsSuccess ? 0 : 1;
    }

    private static string ReadHidden()
    {
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return text.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; continue; }
            if (!char.IsControl(key.KeyChar) && text.Length < 1024) text.Append(key.KeyChar);
        }
    }
}
