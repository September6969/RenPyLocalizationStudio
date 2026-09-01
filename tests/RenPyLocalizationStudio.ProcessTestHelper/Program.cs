var mode = args.FirstOrDefault() ?? "success";
switch (mode)
{
    case "flood":
        for (var index = 0; index < 1000; index++)
        {
            Console.Out.WriteLine($"out-{index}");
            Console.Error.WriteLine($"err-{index}");
        }
        break;
    case "fail":
        Console.Error.WriteLine("expected failure");
        return 17;
    case "sleep":
        await Task.Delay(TimeSpan.FromMinutes(5));
        break;
    case "idle":
        await Task.Delay(1500);
        Console.WriteLine("done");
        break;
    case "environment":
        Console.WriteLine(Environment.GetEnvironmentVariable("RLS_ALLOWED") ?? "missing-allowed");
        Console.WriteLine(Environment.GetEnvironmentVariable("RLS_SHOULD_NOT_LEAK") ?? "missing-secret");
        break;
}
return 0;
