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
        await Task.Delay(400);
        Console.WriteLine("done");
        break;
}
return 0;
