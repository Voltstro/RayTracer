using Eto.Containers;
using Eto.Drawing;
using Eto.Forms;
using LibArarem.Core.ObjectPools;
using RayTracer.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using static RayTracer.Core.MathUtils;
using static Serilog.Log;
using Size = Eto.Drawing.Size;

// using NetFabric.Hyperlinq;

namespace RayTracer.Display.EtoForms;

[SuppressMessage("ReSharper", "PrivateFieldCanBeConvertedToLocalVariable")]
internal sealed class RenderProgressDisplayPanel : Panel
{
	private const int DepthImageWidth = 100;

	/// <summary>Image used for the depth buffer</summary>
	private readonly Bitmap depthBufferBitmap;

	/// <summary>Graphics used for the depth buffer</summary>
	private readonly Graphics depthBufferGraphics;

	private readonly ImageView depthBufferImageView;
	private readonly Pen       depthBufferPen = new(Colors.Gray);

	/// <summary>The actual preview image buffer</summary>
	private readonly Bitmap previewImage;

	/// <summary>Container that draws a border and title around the preview image</summary>
	private readonly GroupBox previewImageContainer;

	/// <summary>The control that holds the preview image</summary>
	private readonly DragZoomImageView previewImageView;

	/// <summary>Render job we are displaying the progress for</summary>
	private readonly AsyncRenderJob renderJob;

	/// <summary>Container that has a title and border around the stats table</summary>
	private readonly GroupBox statsContainer;

	private readonly Timer updatePreviewTimer;

	/// <summary>Time (real-world) at which the last frame update occurred</summary>
	private DateTime prevFrameTime = DateTime.Now; // Assign to `Now` cause otherwise the resulting `deltaT` is crazy high and multiplication makes it overflow later

	/// <summary>Render stats from the last time we updated the preview</summary>
	private RenderStats prevStats;

	/// <summary>How long the last update took to complete (since we can't display how long the current one will take)</summary>
	private TimeSpan prevUpdateDuration;

	/// <summary>Table that contains the various stats</summary>
	private TableLayout statsTable;

	public RenderProgressDisplayPanel(AsyncRenderJob renderJob)
	{
		this.renderJob = renderJob;
		Verbose("Creating StackPanelLayout with content");
		statsTable = new TableLayout
				{ ID = "Stats Table", Size = new Size(0, 0) };
		statsContainer = new GroupBox
				{ ID = "Stats Container", Text = "Statistics", Content = statsTable, Size = new Size(0, 0), MinimumSize = new Size(1, 1) };

		previewImage = new Bitmap(renderJob.RenderOptions.RenderWidth, renderJob.RenderOptions.RenderHeight, PixelFormat.Format24bppRgb)
				{ ID = "Preview Bitmap" };
		previewImageView = new DragZoomImageView
				{ ID = "Preview Image View", Image = previewImage, Size = new Size(0, 0), ZoomButton = MouseButtons.Middle };
		previewImageContainer = new GroupBox
		{
				ID   = "Preview Image Container", Text = "Preview", Content      = previewImageView,
				Size = new Size(0, 0), MinimumSize     = new Size(1, 1), Padding = 10
		};

		depthBufferBitmap = new Bitmap(DepthImageWidth, renderJob.RenderOptions.MaxBounceDepth, PixelFormat.Format32bppRgba)
				{ ID = "Depth Buffer Bitmap" };
		depthBufferImageView = new ImageView
				{ ID = "Image View", Image = depthBufferBitmap, Size = new Size(-1, -1) };
		depthBufferGraphics = new Graphics(depthBufferBitmap)
				{ ID = "Depth Buffer Graphics" };

		Content = new StackLayout
		{
				Items =
				{
						new StackLayoutItem(statsContainer,        VerticalAlignment.Stretch),
						new StackLayoutItem(previewImageContainer, VerticalAlignment.Stretch, true)
				},
				Orientation = Orientation.Horizontal,
				Spacing     = 10,
				ID          = "Main Content StackLayout"
		};
		//Add option to reset image view
		Application.Instance.MainForm.Menu.Items.Add(new Command((_, _) => previewImageView.ResetView()) { MenuText = "Reset Image", ToolTip = "Resets the preview image's size and position to default." });

		//Periodically update the previews using a timer
		//PERF: This creates quite a few allocations when called frequently
		//TODO: Perhaps PeriodicTimer or UITimer
		updatePreviewTimer = new Timer(static state => Application.Instance.Invoke((Action)state!), UpdateAllPreviews, 0, UpdatePeriod);
		prevStats          = new RenderStats(renderJob.RenderOptions); //Kinda arbitrary as long as it's not null
	}

	private static int UpdatePeriod => 1000 / 20; //20 FPS

	/// <summary>
	///  Updates all the previews. Important that it isn't called directly, but by <see cref="Application.Invoke{T}"/> so that it's called on the
	///  main thread
	/// </summary>
	private void UpdateAllPreviews()
	{
		/*
		 * Note that we don't have to worry about locks or anything, since
		 * (A) - It's only called on the main thread
		 * (B) - The timer is only ever reset *after* everything's already been updated
		 */
		RenderStats stats = renderJob.RenderStats;
		try
		{
			Stopwatch sw = Stopwatch.StartNew();
			UpdateImagePreview();
			UpdateStatsTable(stats);
			prevUpdateDuration = sw.Elapsed;
		}
		catch (Exception e)
		{
			ForContext("RenderStats", stats, true).Warning(e, "Exception thrown when updating progress display");
		}
		finally
		{
			prevStats     = new RenderStats(stats);
			prevFrameTime = DateTime.Now;
			Invalidate();
			updatePreviewTimer.Change(UpdatePeriod, -1);
		}
	}

	private void UpdateImagePreview()
	{
		using BitmapData  data         = previewImage.Lock();
		int               xSize        = previewImage.Width, ySize = previewImage.Height;
		ImageFrame<Rgb24> renderBuffer = renderJob.ImageBuffer;
		IntPtr            offset       = data.Data;
		for (int y = 0; y < ySize; y++)
				//This code assumes the source and dest images are same bit depth and size
				//Otherwise here be dragons
			unsafe
			{
				Span<Rgb24> renderBufRow = renderBuffer.GetPixelRowSpan(y);
				void*       destPtr      = offset.ToPointer();
				Span<Rgb24> destRow      = new(destPtr, xSize);

				renderBufRow.CopyTo(destRow);
				offset += data.ScanWidth;
			}
	}

	private void UpdateStatsTable(RenderStats renderStats)
	{
	#region Format Methods

		const string percentFormat  = "p1";
		const string smallNumFormat = "n3";
		const string numFormat      = "n0";
		const int    leftAlign      = 15;
		const int    rightAlign     = 10;
		const int    smallNum       = 1000;

		static char Sign(float f)
		{
			return f switch { < 0 => '-', 0 => ' ', > 0 => '+', _ => '!' };
		}

		static string FormatTime(TimeSpan val)
		{
			return val.Days != 0 ? val.ToString("d'd 'hh':'mm':'ss'.'f").PadLeft(leftAlign) : val.ToString("hh':'mm':'ss'.'f").PadLeft(leftAlign);
		}

		static string FormatTimeSmall(TimeSpan val)
		{
			// ReSharper disable once StringLiteralTypo
			return val.ToString("ss'.'ffffff").PadLeft(leftAlign);
		}

		static string FormatDate(DateTime val)
		{
			return val.ToString("d").PadLeft(leftAlign) + ' ' + val.ToString("h:mm:ss tt").PadRight(rightAlign);
		}

		static string FormatNum(long value)
		{
			return $"{value.ToString(numFormat),leftAlign}";
		}

		static string FormatNumDelta(long curr, long prev, TimeSpan deltaT, string unit = "")
		{
			long   delta  = curr - prev;
			double tRatio = TimeSpan.FromSeconds(1) / deltaT;
			return $"{Sign(delta) + (delta * tRatio).ToString(numFormat),leftAlign} {unit}";
		}

		static string FormatNumWithPercentage(long value, long total)
		{
			return $"{value.ToString(numFormat),leftAlign} {'(' + ((float)value / total).ToString(percentFormat) + ')',rightAlign}";
		}

		static string FormatFloatDelta(float curr, float prev, TimeSpan deltaT, string unit = "")
		{
			float  delta  = curr - prev;
			double tRatio = TimeSpan.FromSeconds(1) / deltaT;
			double res    = delta                   * tRatio;
			return $"{Sign(delta) + res.ToString(res < smallNum ? smallNumFormat : numFormat),leftAlign} {unit}";
		}

		static string FormatFloat(float val)
		{
			return $"{val.ToString(smallNumFormat),leftAlign}";
		}

	#endregion

		List<(string Title, (string Name, string Value, string? Delta)[] NamedValues)> stringStats = new();
		TimeSpan                                                                       deltaT      = DateTime.Now - prevFrameTime;

		{
			TimeSpan elapsed = renderJob.Stopwatch.Elapsed;
			TimeSpan estimatedTotalTime;
			//If the percentage rendered is very low, the division results in a number that's too large to fit in a timespan, which throws
			try
			{
				estimatedTotalTime = elapsed / ((float)renderStats.RawPixelsRendered / renderStats.TotalRawPixels);
			}
			catch (OverflowException)
			{
				estimatedTotalTime = TimeSpan.FromDays(69.420); //If something's broke at least let me have some fun
			}

			stringStats.Add(
					("Time", new (string Name, string Value, string? Delta)[]
					{
							("Elapsed", FormatTime(elapsed), null),
							("Remaining", FormatTime(estimatedTotalTime - elapsed), null),
							("Total", FormatTime(estimatedTotalTime), null),
							("ETC", FormatDate(DateTime.Now + (estimatedTotalTime - elapsed)) + "\t", null) //We need to add a tab here or else the width keeps changing (thanks non-monospaced fonts!)
					})
			);
		}
		{
			long total = renderStats.TotalRawPixels,
				rend   = renderStats.RawPixelsRendered,
				rem    = total - rend;
			const string unit = "px/s";
			stringStats.Add(
					("Raw Pixels", new (string Name, string Value, string? Delta)[]
					{
							("Rendered", FormatNumWithPercentage(rend, total), FormatNumDelta(rend, prevStats.RawPixelsRendered, deltaT, unit)),
							("Remaining", FormatNumWithPercentage(rem, renderStats.TotalRawPixels), null),
							("Total", FormatNum(renderStats.TotalRawPixels), null)
					})
			);
		}
		{
			stringStats.Add(
					("Image", new (string Name, string Value, string? Delta)[]
					{
							//Assumes preview image has same dimensions as render buffer (which should always be the case)
							("Width", FormatNum(previewImage.Width), null),
							("Height", FormatNum(previewImage.Height), null),
							("Pixels", FormatNum(renderStats.TotalTruePixels), null)
					})
			);
		}
		{
			long total = renderJob.RenderOptions.Passes,
				rend   = renderStats.PassesRendered,
				rem    = total - rend;
			long progress = SafeMod(renderStats.RawPixelsRendered, renderStats.TotalTruePixels);
			//Calculate fraction of the passes that was rendered between updates
			float passFrac     = (float)progress                                                        / renderStats.TotalTruePixels;
			float prevPassFrac = (float)SafeMod(prevStats.RawPixelsRendered, prevStats.TotalTruePixels) / prevStats.TotalTruePixels;
			if (passFrac - prevPassFrac < 0) passFrac++; //This just ensures we don't get negatives when calculating the delta (from pass overflow)
			float  fracDelta = passFrac - prevPassFrac;
			double tRatio    = TimeSpan.FromSeconds(1) / deltaT;

			stringStats.Add(
					("Passes", new (string Name, string Value, string? Delta)[]
					{
							("Rendered", FormatNumWithPercentage(rend,     total), FormatFloatDelta(passFrac, prevPassFrac, deltaT, "passes/s")),
							("Remaining", FormatNumWithPercentage(rem,     total), $"{FormatFloat(1f / (float)(fracDelta * tRatio))} sec/pass"),
							("Progress", FormatNumWithPercentage(progress, renderStats.TotalTruePixels), null),
							("Total", FormatNum(total), null)
					})
			);
		} //TODO: Intersection counts
		{
			long total = renderStats.RayCount,
				scat   = renderStats.MaterialScatterCount,
				abs    = renderStats.MaterialAbsorbedCount,
				exceed = renderStats.BounceLimitExceeded,
				sky    = renderStats.SkyRays;
			const string unit = "rays/s";
			stringStats.Add(
					("Rays", new (string Name, string Value, string? Delta)[]
					{
							("Scattered", FormatNumWithPercentage(scat,  total), FormatNumDelta(scat,   prevStats.MaterialScatterCount,  deltaT, unit)),
							("Absorbed", FormatNumWithPercentage(abs,    total), FormatNumDelta(abs,    prevStats.MaterialAbsorbedCount, deltaT, unit)),
							("Exceeded", FormatNumWithPercentage(exceed, total), FormatNumDelta(exceed, prevStats.BounceLimitExceeded,   deltaT, unit)),
							("Sky", FormatNumWithPercentage(sky,         total), FormatNumDelta(sky,    prevStats.SkyRays,               deltaT, unit)),
							("Total", FormatNum(total), FormatNumDelta(total,                           prevStats.RayCount,              deltaT, unit))
					})
			);
		}
		{
			Scene scene = renderJob.Scene;
			stringStats.Add(
					("Scene", new (string Name, string Value, string? Delta)[]
					{
							("Name", $"{scene.Name,leftAlign}", null),
							("Object Count", FormatNum(scene.SceneObjects.Length), null),
							("Light Count", FormatNum(scene.Lights.Length), null)
					})
			);
		}
		{
			stringStats.Add(
					("Renderer", new (string Name, string Value, string? Delta)[]
					{
							("Threads", $"{renderStats.ThreadsRunning.ToString(numFormat)}/{(renderJob.RenderOptions.ConcurrencyLevel == -1 ? "∞" : renderJob.RenderOptions.ConcurrencyLevel.ToString(numFormat))}".PadLeft(leftAlign), null),
							("Completed", $"{renderJob.RenderTask.IsCompleted,leftAlign}", null),
							// ("Task", renderJob.RenderTask.ToString()!, null),
							("Status", $"{renderJob.RenderTask.Status,leftAlign}", null),
							("Max Bounces", FormatNum(renderJob.RenderOptions.MaxBounceDepth), null),
							("KMin", FormatFloat(renderJob.RenderOptions.KMin), null),
							("KMax", FormatFloat(renderJob.RenderOptions.KMax), null),
							("Visualisation", $"{renderJob.RenderOptions.DebugVisualisation,leftAlign}", null)
					})
			);
		}
		{
			const string unit = "/s";
			stringStats.Add(
					("BVH", new (string Name, string Value, string? Delta)[]
					{
							("AABB Misses", FormatNum(renderStats.AabbMisses), FormatNumDelta(renderStats.AabbMisses,                                  prevStats.AabbMisses,            deltaT, unit)),
							("Hittable Misses", FormatNum(renderStats.HittableMisses), FormatNumDelta(renderStats.HittableMisses,                      prevStats.HittableMisses,        deltaT, unit)),
							("Hittable Intersections", FormatNum(renderStats.HittableIntersections), FormatNumDelta(renderStats.HittableIntersections, prevStats.HittableIntersections, deltaT, unit))
					})
			);
		}
		{
			float fps       = (float)(TimeSpan.FromSeconds(1) / deltaT);
			float targetFps = 1000f / UpdatePeriod;
			stringStats.Add(
					("UI", new (string Name, string Value, string? Delta)[]
					{
							("𝚫T", FormatTimeSmall(deltaT), null),
							("FPS", $"{FormatFloat(fps)} {'(' + (fps / targetFps).ToString(percentFormat) + ')',rightAlign}", null),
							("Target", $"{FormatFloat(targetFps)} FPS", null),
							("Upd Duration", FormatTimeSmall(prevUpdateDuration                                               * 1000) + " ms", null),
							("Delay", FormatTimeSmall((deltaT - prevUpdateDuration - TimeSpan.FromMilliseconds(UpdatePeriod)) * 1000) + " ms", null)
					})
			);
		}

		//Due to how the table is implemented, I can't resize it later
		//So if the size doesn't match our array, we need to recreate it
		//Columns are for Title, Names, Values, Deltas
		Size correctDims = new(4, stringStats.Count + 2); //+3 = 1 for depth + 1 for spacer row + 1 for column titles
		if (statsTable.Dimensions != correctDims)
		{
			Verbose("Old table dims {Dims} do not match stats array, disposing and recreating with dims {NewDims}", statsTable.Dimensions, correctDims);
			statsTable.Detach();
			statsTable.Dispose();
			statsTable = new TableLayout(correctDims)
			{
					ID = "Stats Table", Spacing = new Size(10, 10), Padding = 10, Size = new Size(0, 0)
			};
			statsContainer.Content = statsTable;
		}

		//Headers
		{
			static Label CreateHeaderLabel()
			{
				return new Label { Style = Appearance.Styles.GeneralTextualBold };
			}

			TableRow row = statsTable.Rows[0];
			//Get the Labels at the correct locations, or assign them if needed
			if (row.Cells[0].Control is not Label titleLabel)
			{
				Verbose("Cell [{Position}] was not label (was {Control}), disposing and updating", (0, 0), row.Cells[0].Control);
				row.Cells[0]?.Control?.Detach();
				row.Cells[0]?.Control?.Dispose(); //Dispose the old control
				titleLabel = CreateHeaderLabel();
				statsTable.Add(titleLabel, 0, 0);
			}

			if (row.Cells[1].Control is not Label nameLabel)
			{
				Verbose("Cell [{Position}] was not name label (was {Control}), disposing and updating", (1, 0), row.Cells[1].Control);
				row.Cells[1]?.Control?.Detach();
				row.Cells[1]?.Control?.Dispose(); //Dispose of the old control
				nameLabel = CreateHeaderLabel();
				statsTable.Add(nameLabel, 1, 0);
			}

			if (row.Cells[2].Control is not Label valueLabel)
			{
				Verbose("Cell [{Position}] was not value label (was {Control}), disposing and updating", (2, 0), row.Cells[2].Control);
				row.Cells[2]?.Control?.Detach();
				row.Cells[2]?.Control?.Dispose(); //Dispose of the old control
				valueLabel = CreateHeaderLabel();
				statsTable.Add(valueLabel, 2, 0);
			}

			if (row.Cells[3].Control is not Label deltaLabel)
			{
				Verbose("Cell [{Position}] was not delta label (was {Control}), disposing and updating", (3, 0), row.Cells[3].Control);
				row.Cells[3]?.Control?.Detach();
				row.Cells[3]?.Control?.Dispose(); //Dispose of the old control
				deltaLabel = CreateHeaderLabel();
				statsTable.Add(deltaLabel, 3, 0);
			}

			titleLabel.Text = "Category";
			nameLabel.Text  = "Statistic";
			valueLabel.Text = "Value";
			deltaLabel.Text = "Delta";
		}

		{
			//String stats
			for (int i = 0; i < stringStats.Count; i++)
			{
				int      rowIdx = i + 1; //Account for headers
				TableRow row    = statsTable.Rows[rowIdx];
				(string title, (string Name, string Value, string? Delta)[] namedValues) = stringStats[i];
				//Aggregate the name and value texts
				string aggregatedNames  = StringBuilderPool.BorrowInline(static (sb, namedValues) => { sb.AppendJoin(Environment.NewLine, namedValues.Select(val => $"{val.Name}:")); },            namedValues);
				string aggregatedValues = StringBuilderPool.BorrowInline(static (sb, namedValues) => { sb.AppendJoin(Environment.NewLine, namedValues.Select(val => val.Value)); },                 namedValues);
				string aggregatedDeltas = StringBuilderPool.BorrowInline(static (sb, namedValues) => { sb.AppendJoin(Environment.NewLine, namedValues.Select(val => val.Delta ?? string.Empty)); }, namedValues);

				//Get the Labels at the correct locations, or assign them if needed
				if (row.Cells[0].Control is not Label titleLabel)
				{
					Verbose("Cell [{Position}] was not label (was {Control}), disposing and updating", (0, rowIdx: rowIdx), row.Cells[0].Control);
					row.Cells[0]?.Control?.Detach();
					row.Cells[0]?.Control?.Dispose(); //Dispose the old control
					titleLabel = new Label { Style = Appearance.Styles.GeneralTextualUnderline };
					statsTable.Add(titleLabel, 0, rowIdx);
				}

				if (row.Cells[1].Control is not Label nameLabel)
				{
					Verbose("Cell [{Position}] was not name label (was {Control}), disposing and updating", (1, rowIdx: rowIdx), row.Cells[1].Control);
					row.Cells[1]?.Control?.Detach();
					row.Cells[1]?.Control?.Dispose(); //Dispose of the old control
					nameLabel = new Label { Style = Appearance.Styles.GeneralTextual };
					statsTable.Add(nameLabel, 1, rowIdx);
				}

				if (row.Cells[2].Control is not Label valueLabel)
				{
					Verbose("Cell [{Position}] was not value label (was {Control}), disposing and updating", (2, rowIdx: rowIdx), row.Cells[2].Control);
					row.Cells[2]?.Control?.Detach();
					row.Cells[2]?.Control?.Dispose(); //Dispose of the old control
					valueLabel = new Label { Style = Appearance.Styles.ConsistentTextWidth };
					statsTable.Add(valueLabel, 2, rowIdx);
				}

				if (row.Cells[3].Control is not Label deltaLabel)
				{
					Verbose("Cell [{Position}] was not delta label (was {Control}), disposing and updating", (3, rowIdx: rowIdx), row.Cells[3].Control);
					row.Cells[3]?.Control?.Detach();
					row.Cells[3]?.Control?.Dispose(); //Dispose of the old control
					deltaLabel = new Label { Style = Appearance.Styles.ConsistentTextWidth };
					statsTable.Add(deltaLabel, 3, rowIdx);
				}

				titleLabel.Text = title;
				nameLabel.Text  = aggregatedNames;
				valueLabel.Text = aggregatedValues;
				deltaLabel.Text = aggregatedDeltas;
			}
		}

		//Depth buffer
		{
			int       row             = statsTable.Dimensions.Height - 1;
			int       maxDepth        = renderJob.RenderOptions.MaxBounceDepth;
			TableCell titleCell       = statsTable.Rows[row].Cells[0];
			TableCell descriptionCell = statsTable.Rows[row].Cells[1];
			TableCell depthBufferCell = statsTable.Rows[row].Cells[2];
			//No delta cell

			//Update title control type if needed
			if (titleCell.Control is not Label titleLabel)
			{
				Verbose("Depth Buffer Title Cell {Position} was not label (was {Control}), disposing and updating", (0, row), titleCell.Control);
				titleCell.Control?.Detach();
				titleCell.Control?.Dispose(); //Dispose of the old control
				statsTable.Add(titleLabel = new Label(), 0, row);
			}

			titleLabel.Text = "Depth Buffer"; //Update title control text

			//Update description control if needed
			if (descriptionCell.Control is not Label descLabel)
			{
				Verbose("Depth Buffer Description Cell {Position} was not Label (was {Control}), disposing and updating", (1, row), depthBufferCell.Control);
				depthBufferCell.Control?.Detach();
				depthBufferCell.Control?.Dispose(); //Dispose of the old control
				statsTable.Add(descLabel = new Label(), 1, row);
			}

			descLabel.Text = $"{depthBufferBitmap.Width}x{depthBufferBitmap.Height}";
			//Update image control if needed
			if (depthBufferCell.Control != depthBufferImageView)
			{
				Verbose("Depth Buffer Image Cell {Position} was not our ImageView (was {Control}), disposing and updating", (2, row), depthBufferCell.Control);
				depthBufferCell.Control?.Detach();
				depthBufferCell.Control?.Dispose(); //Dispose of the old control
				statsTable.Add(depthBufferImageView, 2, row);
			}

			//What I'm doing here is adjusting the depth values so that the largest one reaches the end of the graph (scaling up to fill the image)
			double[] doubleDepths = ArrayPool<double>.Shared.Rent(maxDepth);
			double   max          = 0;
			for (int depth = 0; depth < maxDepth; depth++) //Calculate the fractions and the max
			{
				#if true //Toggle whether to use a log function to compress the chart. Mostly needed when we have high max depth values
				const double b        = .0000001;
				double       m        = renderStats.RayCount;
				double       fraction = Math.Log((b * renderStats.RawRayDepthCounts[depth]) + 1, m) / Math.Log((b * m) + 1, m); //https://www.desmos.com/calculator/erite0if8u
				#else
				double fraction = renderStats.RawRayDepthCounts[depth] / renderStats.RawRayDepthCounts.Sum(u => (double)u); //https://www.desmos.com/calculator/erite0if8u
				#endif

				doubleDepths[depth] = fraction;
				max                 = Math.Max(max, fraction);
			}

			depthBufferGraphics.Clear();
			for (int depth = 0; depth < maxDepth; depth++)
			{
				double corrected = doubleDepths[depth] / max; //Adjust so the max == 1
				int    endX      = (int)Math.Min(corrected * DepthImageWidth, DepthImageWidth);
				depthBufferGraphics.DrawLine(depthBufferPen, 0, depth, endX, depth);
			}

			//Flush the image or it might not be drawn
			depthBufferGraphics.Flush();
		}

		prevStats = renderJob.RenderStats;
	}

	/// <inheritdoc/>
	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
		updatePreviewTimer.Dispose();
	}
}