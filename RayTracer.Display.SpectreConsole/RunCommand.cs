using JetBrains.Annotations;
using RayTracer.Core;
using RayTracer.Core.Debugging;
using RayTracer.Impl;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using static Spectre.Console.AnsiConsole;
using Color = Spectre.Console.Color;
using Console = System.Console;
using Markup = Spectre.Console.Markup;

#pragma warning disable CS8765

namespace RayTracer.Display.SpectreConsole;

[PublicAPI]
[NoReorder]
internal sealed class RunCommand : AsyncCommand<RunCommand.Settings>
{
#region Markup Styles

	/// <summary>Most important - style for the app title text</summary>
	private const string AppTitleMarkup = "bold red underline";

	/// <summary>Markup for the title of an <see cref="IRenderable"/></summary>
	private const string TitleMarkup = "bold blue";

	/// <summary>Markup for the heading of a table</summary>
	private const string HeadingMarkup = "italic blue";

	/// <summary>Markup for when displaying a scene name/selection</summary>
	private const string SceneMarkup = "italic";

	/// <summary>Markup for the "Rendering..." animation</summary>
	private const string RenderingAnimationMarkup = "italic green";

	private const string StatsCategoryMarkup  = "bold";
	private const string FinishedRenderMarkup = "bold underline";

#endregion

	/// <summary>Displays and confirms the settings passed in by the user</summary>
	/// <returns>The confirmed scene and render options, to use for render execution</returns>
	private static (Scene Scene, RenderOptions Options) ConfirmSettings(CommandContext context, Settings settings)
	{
		RenderOptions renderOptions = new(settings.Width, settings.Height, settings.KMin, settings.KMax, settings.Concurrency, settings.Passes, settings.MaxDepth, settings.DebugVisualisation);
		//Print settings to console
		{
			Table table = new()
			{
					Title = new TableTitle($"[{TitleMarkup}]Provided Options:[/]")
			};
			//Headings for the columns
			table.AddColumn($"[{HeadingMarkup}]Option[/]");
			table.AddColumn($"[{HeadingMarkup}]Value[/]");

			// foreach (PropertyInfo propertyInfo in typeof(Settings).GetProperties())
			//		table.AddRow(propertyInfo.Name, $"[italic]{propertyInfo.GetValue(settings)?.ToString() ?? "<null>"}[/]");
			foreach (PropertyInfo propertyInfo in typeof(RenderOptions).GetProperties())
				table.AddRow(propertyInfo.Name, $"[italic]{propertyInfo.GetValue(renderOptions)?.ToString() ?? "<null>"}[/]");
			Write(table);
		}


		//Select scene
		Scene scene = Prompt(
				new SelectionPrompt<Scene>()
						.Title($"[{TitleMarkup}]Please select which scene you wish to load:[/]")
						.AddChoices(BuiltinScenes.GetAll())
						.UseConverter(s => $"[{SceneMarkup}]{s}[/]")
		);
		MarkupLine($"Selected scene is [{SceneMarkup}]{scene}[/]");
		return (scene, renderOptions);
	}

	/// <summary>Creates a little live display for while the render is running</summary>
	private static async Task DisplayProgress(AsyncRenderJob renderJob)
	{
		const int interval = 1000; //How long between updates of the live display

		//First thing is the title
		string appTitle = $"[{AppTitleMarkup}]RayTracer v{typeof(Scene).Assembly.GetName().Version} - [{SceneMarkup}]{renderJob.Scene.Name}[/][/]";
		Console.Title = Markup.Remove(appTitle);


		//Keep re-calling the display method until the render is completed
		//This way, we can reset the display if anything goes funky
		while (!renderJob.RenderCompleted)
			await Live(new Markup("[bold red slowblink]Live Display Starting...[/]")).StartAsync(new LiveDisplayClosure(appTitle, renderJob, interval).LiveDisplayAsync);
	}

	private sealed record LiveDisplayClosure(string AppTitle, AsyncRenderJob RenderJob, int Interval)
	{
		private readonly (int W, int H) prevDims = (Console.WindowWidth, Console.WindowHeight);

		/*
		 * NOTE: If this method returns and the render job isn't completed, a new closure is created and the method is called again
		 * This allows us to 'reset' the state of everything
		 * I could also just use a `goto` at the start of the method call, but this would render hot reload useless for the method
		 */
		internal async Task LiveDisplayAsync(LiveDisplayContext ctx)
		{
			//Clear and create a new table (reset)
			Clear();
			Table statsAndImageTable = new()
			{
					Border      = new DoubleTableBorder(),
					BorderStyle = new Style(Color.Blue),
					Title       = new TableTitle(AppTitle),
					Alignment   = Justify.Center
			};
			statsAndImageTable.AddColumns(
					new TableColumn($"[{HeadingMarkup}]Render Statistics[/]\n").Centered(),
					new TableColumn($"[{HeadingMarkup}]Image Preview[/]\n").Centered()
			);
			//Tell the live display this is the new target to render
			ctx.UpdateTarget(statsAndImageTable);

			//Inner loop doesn't flicker (gasp)
			while (!RenderJob.RenderCompleted)
			{
				//Automatically reset if dimensions changed
				(int W, int H) currDims = (Console.WindowWidth, Console.WindowHeight);
				if (prevDims != currDims) return;
				UpdateLiveDisplay(statsAndImageTable, RenderJob);
				ctx.Refresh();
				await Task.Delay(Interval);
				//Allow for a manual reset using the 'C' key
				if (Console.KeyAvailable && (Console.ReadKey(true).Key == ConsoleKey.C)) return;
			}
		}
	}

	private static void UpdateLiveDisplay(Table statsAndImageTable, AsyncRenderJob renderJob)
	{
		statsAndImageTable.Rows.Clear();

	#region Rendering... animation

		StringBuilder sb = new(100);
		const double  f  = 2.5; //Total time per ellipsis cycle (s)
		const double  a  = 5;   //Max ellipses per cycle

		int    n;
		double sec = renderJob.Stopwatch.Elapsed.TotalSeconds;
		#if true
		//Triangle wave, goes up and down
		{
			double sin    = Math.Sin((sec / f) * Math.PI);
			double inv    = Math.Asin(sin);
			double abs    = Math.Abs(inv);
			double scaled = ((abs * a) / Math.PI) * 2;
			n = (int)Math.Round(scaled);
		}
		#else
			//Sawtooth wave
			{
				//Get fractional part of the
				double frac = (sec / f) % 1;
				double scaled = frac      * (a + 1);
				n = (int)Math.Floor(scaled);
			}
		#endif
		sb.Clear();
		sb.Append($"[{RenderingAnimationMarkup}]");
		sb.Append(' ', n); //Pad/centre string
		sb.Append("Rendering");
		sb.Append('.', n);
		sb.Append("[/]");

		statsAndImageTable.Caption = new TableTitle(sb.ToString());

	#endregion

	#region Image buffer display

		//The image that shows the current render buffer
		//Make sure we don't exceed the vertical space limit when trying to maximise the width
		//FIXME: These sizing thingies don't really work too well on some resolutions
		int                   maxHeight       = Console.WindowHeight - 6; //The offset is so that we leave enough room for the title (1) + heading (2) + caption (1) + newline (1) = 5
		float                 aspect          = (float)renderJob.ImageBuffer.Width / renderJob.ImageBuffer.Height;
		int                   maxWidth        = (int)(maxHeight * aspect);
		CustomImageRenderable imageRenderable = new(renderJob.Image) { Resampler = KnownResamplers.Robidoux, MaxConsoleWidth = maxWidth - 6 };

	#endregion

	#region Render stats table

		Table renderStatsTable = new Table { Border = new NoTableBorder() }.AddColumns($"[{HeadingMarkup}]Property[/]", $"[{HeadingMarkup}]Value[/]").HideHeaders(); //Add the headers so the column count is correct, but we don't want them shown

		int           totalTruePixels = renderJob.RenderStats.TotalTruePixels;
		long          totalRawPix     = renderJob.RenderStats.TotalRawPixels;
		long          rayCount        = renderJob.RenderStats.RayCount;
		RenderOptions options         = renderJob.RenderOptions;
		int           totalPasses     = options.Passes;
		TimeSpan      elapsed         = renderJob.Stopwatch.Elapsed;

		float    percentageRendered = (float)renderJob.RenderStats.RawPixelsRendered / totalRawPix;
		long     rawPixelsRemaining = totalRawPix - renderJob.RenderStats.RawPixelsRendered;
		int      passesRemaining    = totalPasses - renderJob.RenderStats.PassesRendered;
		TimeSpan estimatedTotalTime;
		//If the percentage rendered is very low, the division results in a number that's too large to fit in a timespan, which throws
		try
		{
			estimatedTotalTime = elapsed / percentageRendered;
		}
		catch (OverflowException)
		{
			estimatedTotalTime = TimeSpan.FromDays(69.420); //If something's broke at least let me have some fun
		}

		const string timeFormat     = "h\\:mm\\:ss"; //Format string for Timespan
		const string dateTimeFormat = "G";           //Format string for DateTime
		const string percentFormat  = "p1";          //Format string for percentages
		const string numFormat      = "n0";
		const int    numAlign       = 15;
		const int    percentAlign   = 8;

		renderStatsTable.Rows.Clear();
		renderStatsTable.AddRow($"[{StatsCategoryMarkup}]Time[/]",         $"{elapsed.ToString(timeFormat)} elapsed");
		renderStatsTable.AddRow("",                                        $"{(estimatedTotalTime - elapsed).ToString(timeFormat)} remaining");
		renderStatsTable.AddRow("",                                        $"{estimatedTotalTime.ToString(timeFormat)} total");
		renderStatsTable.AddRow("",                                        $"{(DateTime.Now + (estimatedTotalTime - elapsed)).ToString(dateTimeFormat)} ETC");
		renderStatsTable.AddRow("",                                        "");
		renderStatsTable.AddRow($"[{StatsCategoryMarkup}]Pixels (Raw)[/]", $"{FormatL(renderJob.RenderStats.RawPixelsRendered, totalRawPix)} rendered");
		renderStatsTable.AddRow("",                                        $"{FormatL(rawPixelsRemaining,                      totalRawPix)} remaining");
		renderStatsTable.AddRow("",                                        $"{totalRawPix.ToString(numFormat),numAlign}          total");
		renderStatsTable.AddRow("",                                        "");
		renderStatsTable.AddRow($"[{StatsCategoryMarkup}]Image [/]",       $"{totalTruePixels.ToString(numFormat),numAlign}          pixels total");
		renderStatsTable.AddRow("",                                        $"{options.Width.ToString(numFormat),numAlign}          pixels wide");
		renderStatsTable.AddRow("",                                        $"{options.Height.ToString(numFormat),numAlign}          pixels high");
		renderStatsTable.AddRow("",                                        "");
		renderStatsTable.AddRow($"[{StatsCategoryMarkup}]Passes[/]",       $"{FormatI(renderJob.RenderStats.PassesRendered, totalPasses)} rendered");
		renderStatsTable.AddRow("",                                        $"{FormatI(passesRemaining,                      totalPasses)} remaining");
		renderStatsTable.AddRow("",                                        $"{totalPasses.ToString(numFormat),numAlign}          total");
		renderStatsTable.AddRow("",                                        "");
		renderStatsTable.AddRow($"[{StatsCategoryMarkup}]Rays[/]",         $"{FormatL(renderJob.RenderStats.MaterialScatterCount,  rayCount)} scattered");
		renderStatsTable.AddRow("",                                        $"{FormatL(renderJob.RenderStats.MaterialAbsorbedCount, rayCount)} absorbed");
		renderStatsTable.AddRow("",                                        $"{FormatL(renderJob.RenderStats.BounceLimitExceeded,   rayCount)} exceeded");
		renderStatsTable.AddRow("",                                        $"{FormatL(renderJob.RenderStats.SkyRays,               rayCount)} sky");
		renderStatsTable.AddRow("",                                        $"{rayCount.ToString(numFormat),numAlign}          total");
		renderStatsTable.AddRow("",                                        "");
		renderStatsTable.AddRow($"[{StatsCategoryMarkup}]Scene[/]",        $"[{SceneMarkup}]{renderJob.Scene}[/]");
		renderStatsTable.AddRow("",                                        $"{renderJob.Scene.Camera}");
		renderStatsTable.AddRow("",                                        $"{renderJob.Scene.SkyBox}");
		renderStatsTable.AddRow("",                                        "");
		// ReSharper disable once ConvertIfStatementToConditionalTernaryExpression
		if (options.ConcurrencyLevel != -1) //Don't want to divide by a negative
			renderStatsTable.AddRow($"[{StatsCategoryMarkup}]Renderer[/]", $"{FormatI(renderJob.RenderStats.ThreadsRunning, options.ConcurrencyLevel)} threads");
		else
			renderStatsTable.AddRow($"[{StatsCategoryMarkup}]Renderer[/]", $"{renderJob.RenderStats.ThreadsRunning.ToString(numFormat),numAlign} threads");
		renderStatsTable.AddRow("", "");
		// renderStatsTable.AddRow($"[{StatsCategoryMarkup}]Console[/]",      $"CWin: ({Console.WindowWidth}x{Console.WindowHeight})");
		// renderStatsTable.AddRow("",                                        $"CBuf: ({Console.BufferWidth}x{Console.BufferHeight})");
		// renderStatsTable.AddRow("",                                        $"Ansi: ({AnsiConsole.Console.Profile.Width}x{AnsiConsole.Console.Profile.Height})");
		//Because we'll probably have a crazy sized depth buffer, group the indices together
		List<(Range range, long count)> depths = new();
		BarChart                        chart  = new() { Width = 45, MaxValue = null, ShowValues = false };
		//Group the raw buffer into our aggregated one
		float grouping = 0.5f; //How many depths to combine into a group
		int   rawIndex = 0;    //Where we are in the raw (ungrouped) index buffer
		while (true)
		{
			int  start = rawIndex;
			int  end   = start;
			long count = 0;
			for (int i = 0; i < grouping; i++)
			{
				if (rawIndex >= renderJob.RenderStats.RawRayDepthCounts.Length)
					break;
				count += renderJob.RenderStats.RawRayDepthCounts[rawIndex];
				rawIndex++;
				end = rawIndex;
			}

			depths.Add((start..end, count));
			grouping = (grouping * 1.5f) + .1f;
			// grouping += 0f;
			if (rawIndex >= renderJob.RenderStats.RawRayDepthCounts.Length)
				break;
		}

		if (depths.Count == 0) chart.AddItem("[bold red]Error: No depth values[/]", 0, Color.Red);
		#if !true //Toggle whether to use a log function to compress the chart. Mostly needed when we have high max depth values
		const double b = 0.000003;
		double       m = rayCount;
		for (int i = 0; i < depths.Count; i++)
			chart.AddItem(
					$"[[{depths[i].range}]]",
					Math.Log((b * depths[i].count) + 1, m) / Math.Log((b * m) + 1, m), //https://www.desmos.com/calculator/erite0if8u
					Color.White
			);
		#else
		for (int i = 0; i < depths.Count; i++)
			chart.AddItem(
					$"[[{depths[i].range}]]",
					depths[i].count / renderJob.RenderStats.RawRayDepthCounts.Sum(u => (double)u), //https://www.desmos.com/calculator/erite0if8u
					Color.White
			);
		#endif
		renderStatsTable.AddRow(new Markup($"[{StatsCategoryMarkup}]Depth Buffer[/]"), chart);


		static string FormatL(long val, long total)
		{
			return $"{val.ToString(numFormat),numAlign} {'(' + ((float)val / total).ToString(percentFormat) + ')',percentAlign}";
		}

		static string FormatI(int val, int total)
		{
			return $"{val.ToString(numFormat),numAlign} {'(' + ((float)val / total).ToString(percentFormat) + ')',percentAlign}";
		}

	#endregion

	#region Putting it all together

		statsAndImageTable.AddRow(renderStatsTable /* */, imageRenderable /**/);

	#endregion
	}

	/// <summary>Function to be called once a render job is finished. Also returns the rendered image</summary>
	private static Image<Rgb24> FinalizeRenderJob(AsyncRenderJob renderJob)
	{
		Image<Rgb24> image = renderJob.Image;
		MarkupLine($"[{FinishedRenderMarkup}]Finished Rendering in {renderJob.Stopwatch.Elapsed:h\\:mm\\:ss}[/]");

		//Print any errors
		if (!GraphicsValidator.Errors.IsEmpty)
		{
			ConcurrentDictionary<GraphicsErrorType, ConcurrentDictionary<object, ulong>> errors = GraphicsValidator.Errors;
			//Print a list of all the errors that occurred
			Table table = new()
			{
					Title = new TableTitle($"[{TitleMarkup}]Errors occured during render:[/]")
			};
			//I chose to have the error type on the top (column) and the object on the left (row)
			//We have to build a comprehensive list of all possible rows and columns that occur in all dimensions/levels of the dictionaries (not all objects will exist for all error types and vice versa)
			GraphicsErrorType[] allErrorTypes = Enum.GetValues<GraphicsErrorType>(); //All possible enum values for the types of error that can occur
			HashSet<object>     allObjects    = new();                               //Aggregated of all the objects that had any errors (can be just one type of error or all types)

			//Important to create all the columns first, before we create the rows, or we get exceptions (not enough columns)
			table.AddColumn($"[{HeadingMarkup}]Erroring object[/]");
			foreach (GraphicsErrorType errorType in allErrorTypes) table.AddColumn($"[{HeadingMarkup}]{errorType}[/]");
			//Aggregate all the objects that had errors
			foreach (GraphicsErrorType errorType in allErrorTypes)
			{
				if (!errors.TryGetValue(errorType, out ConcurrentDictionary<object, ulong>? objectMap)) continue;
				foreach (object obj in objectMap.Keys) allObjects.Add(obj);
			}

			//Build the rows. Each row represents the erroring object, and the error counts for it
			IRenderable[] row            = new IRenderable[allErrorTypes.Length + 1];
			Markup        noErrorsMarkup = new("[dim italic green]N/A[/]"); //The error never occurred for this object
			foreach (object obj in allObjects)
			{
				//Fill the array with error messages so that I can tell when i mess up, also saves the app from crashing if that happens (because the default is null)
				Array.Fill(row, new Markup("[bold red underline rapidblink]INTERNAL ERROR[/]"));
				row[0] = new Markup(Markup.Escape(obj.ToString()!)); //First item is the object's name
				//Calculate all the columns (error count values) for the current object. Important that the loop is shifted +1 so that i=0 is the object name
				for (int i = 1; i <= allErrorTypes.Length; i++)
				{
					GraphicsErrorType errorType = allErrorTypes[i - 1];
					//Try get the error count, and if one doesn't exist then we know it's 0
					if (!errors.ContainsKey(errorType))
					{
						row[i] = noErrorsMarkup;
						continue;
					}

					bool exists = errors[errorType].TryGetValue(obj, out ulong count);
					if (!exists || (count == 0)) row[i] = noErrorsMarkup;
					//Change the count text's colour depending on how large the count is
					else
						row[i] = new Markup(
								@$"[{count switch
								{
										< 100   => "#afd700",
										< 500   => "#ffff00",
										< 1000  => "#ffaf00",
										< 2500  => "#ff8700",
										< 5000  => "#ff5f00",
										< 10000 => "#ff2f00",
										_       => "#ff0000"
								}} bold]{count}[/]"
						);
				}

				table.AddRow(row);
			}

			Write(table);
		}
		else
		{
			MarkupLine($"[{FinishedRenderMarkup}]No errors occured during render[/]");
		}

		return image;
	}

	/// <inheritdoc/>
	public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
	{
		//Get the settings for how and what we'll render
		(Scene scene, RenderOptions renderOptions) = ConfirmSettings(context, settings);

		//Start the render job and display the progress while we wait
		AsyncRenderJob renderJob  = new(scene, renderOptions);
		Task           renderTask = renderJob.StartOrGetRenderAsync();
		await DisplayProgress(renderJob);
		await renderTask; //Just in case DisplayProgress returned early

		//Finalize everything
		Image<Rgb24> image = FinalizeRenderJob(renderJob);

		//Save and open the image for viewing
		await image.SaveAsync(File.OpenWrite(settings.OutputFile), new PngEncoder())!;
		try
		{
			await Process.Start(
					new ProcessStartInfo
					{
							FileName  = "eog",
							Arguments = $"\"{settings.OutputFile}\"",
							//These flags stop the image display program's console from attaching to ours (because that's yuck!)
							UseShellExecute        = false,
							RedirectStandardError  = true,
							RedirectStandardInput  = true,
							RedirectStandardOutput = true
					}
			)!.WaitForExitAsync();
		}
		catch (Exception)
		{
			WriteLine("Could not start image viewer program");
		}

		return 0;
	}

	[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
	internal sealed class Settings : CommandSettings
	{
		//TODO: Tie into RenderOptions.Default
		[Description("How many pixels wide the image should be")]
		[CommandOption("-w|--width")]
		[DefaultValue(1920)]
		public int Width { get; init; }

		[Description("How many pixels high the image should be")]
		[CommandOption("-h|--height")]
		[DefaultValue(1080)]
		public int Height { get; init; }

		// ReSharper disable once StringLiteralTypo
		[Description("Minimum distance along the ray to check for intersections with objects")]
		[CommandOption("--kmin")]
		[DefaultValue(0.001f)]
		public float KMin { get; init; }

		// ReSharper disable once StringLiteralTypo
		[Description("Maximum distance along the ray to check for intersections with objects")]
		[CommandOption("--kmax")]
		[DefaultValue(float.PositiveInfinity)]
		public float KMax { get; init; }

		[Description("The output path for the rendered image")]
		[CommandOption("-o|--output|--output-file")]
		[DefaultValue("./image.png")]
		public string OutputFile { get; init; } = null!;

		[Description("How many render passes to average (gives less noisy results)")]
		[CommandOption("-p|--passes")]
		[DefaultValue(100)]
		public int Passes { get; init; }

		[Description("Flag for enabling debugging visualisations, such as surface normals")]
		[CommandOption("--debug|--visualise")]
		[DefaultValue(GraphicsDebugVisualisation.None)]
		public GraphicsDebugVisualisation DebugVisualisation { get; init; }

		[Description("Changes the maximum number of threads that can run at a time")]
		[CommandOption("-c|--threads|--concurrency")]
		[DefaultValue(-1)]
		public int Concurrency { get; init; }

		[Description("Maximum number of times a ray can bounce (max depth it can reach)")]
		[CommandOption("-d|--depth|--max-depth")]
		[DefaultValue(100)]
		public int MaxDepth { get; init; }
	}
}