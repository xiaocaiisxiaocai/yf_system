using Yf.Api.Infrastructure;

var app = await ApiApplication.BuildAsync(args);
if (app is not null) await app.RunAsync();

public partial class Program;
