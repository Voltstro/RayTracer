using Eto.Drawing;
using Eto.Forms;
using RayTracer.Core;
using RayTracer.Impl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static Serilog.Log;

namespace RayTracer.Display.EtoForms;

/// <summary>Panel that allows modification of the settings of a <see cref="RenderOptions"/> instance</summary>
internal sealed class RenderOptionSelectorPanel : Panel
{
	/// <summary>Creates a new <see cref="RenderOptionSelectorPanel"/> panel</summary>
	/// <param name="click"></param>
	/// <param name="options">
	///  Optional initial value for the render options. If <see langword="null"/>, will be set to the default
	/// </param>
	public RenderOptionSelectorPanel(EventHandler<EventArgs> click, RenderOptions? options = null)
	{
		if (options == null)
		{
			Verbose("No options supplied, using default value {DefaultValue}", RenderOptions = new RenderOptions());
		}
		else
		{
			Verbose("Custom render options supplied: {RenderOptions}", options);
			RenderOptions = options;
		}

		Verbose("Creating new table layout");
		TableLayout tableLayout = new() { Padding = 10, Spacing = new Size(10, 5) };
		//Loop over each property in RenderOptions and create an editor for it
		Verbose("Creating property editors");
		foreach (PropertyInfo prop in typeof(RenderOptions).GetProperties())
		{
			PropertyEditorView editorView;
			Label              label = new() { ID = $"{prop.Name} label", Text = prop.Name, Style = Appearance.Styles.GeneralTextual };

			TableCell labelCell  = new(label);
			TableCell editorCell = new();
			tableLayout.Rows!.Add(new TableRow(labelCell, editorCell) { ScaleHeight = false });

			//Switch to create the editor
			if (prop.PropertyType == typeof(int))
			{
				editorView = new IntEditor(this, prop, editorCell);
			}
			else if (prop.PropertyType == typeof(float))
			{
				editorView = new FloatEditor(this, prop, editorCell);
			}
			else if (prop.PropertyType.IsEnum)
			{
				//Hey it's a little funky but it works
				editorView = (PropertyEditorView)Activator.CreateInstance(typeof(EnumEditor<>).MakeGenericType(prop.PropertyType), this, prop, editorCell)!;
			}
			else
			{
				editorCell.Control = new Label { Text = $"{prop.PropertyType} not yet supported sorry!", ID = $"{prop.Name} error message" };
				continue;
			}

			Verbose("Created editor for property {Property}: {Editor}", prop, editorView);
			renderOptionsPropertyEditors.Add(editorView);
		}

		tableLayout.Rows!.Add(new TableRow()); //Add empty row so that scaling looks nice (last row sizes to fil gap)

		Verbose("Creating start render button");
		Button startRenderButton = new(click) { Text = "Start Render", ToolTip = "Starts the render job with the specified render options", ID = "Start render button", Style = Appearance.Styles.GeneralTextual };

		Verbose("Creating scene selection dropdown");
		Scene[] scenes = BuiltinScenes.GetAll().ToArray();
		Verbose("Builtin scenes are: {BuiltinScenes}", scenes);
		sceneSelectDropdown = new DropDown
		{
				DataStore = scenes, ToolTip = "Select a scene to be rendered", ID = "Scene select dropdown", Style = Appearance.Styles.GeneralTextual
		};
		//Select the first scene by default
		sceneSelectDropdown.SelectedValue = Scene = scenes[0];
		sceneSelectDropdown.SelectedValueChanged += (_, _) =>
		{
			Scene = (Scene)sceneSelectDropdown.SelectedValue;
			Verbose("Scene property changed to {Value}", Scene);
		};

		Verbose("Creating StackPanelLayout for content");
		Content = new StackLayout
		{
				Items =
				{
						new StackLayoutItem(tableLayout,         HorizontalAlignment.Stretch),
						new StackLayoutItem(sceneSelectDropdown, HorizontalAlignment.Stretch),
						new StackLayoutItem(startRenderButton,   HorizontalAlignment.Stretch)
				},
				Orientation = Orientation.Vertical,
				Spacing     = 10,
				ID          = "Main Content StackLayout"
		};

		UpdateRenderOptionEditorsFromVariable();
	}

#region Property Editors

	private sealed class IntEditor : PropertyEditorView
	{
		private readonly NumericStepper stepper;

		/// <inheritdoc/>
		public IntEditor(RenderOptionSelectorPanel target, PropertyInfo prop, TableCell tableCell) : base(target, prop, tableCell)
		{
			const int min = int.MinValue;
			const int max = int.MaxValue;
			// if (typeof(RenderOptions).GetConstructors()[0].GetParameters().FirstOrDefault(p => p.Name == prop.Name)?.GetCustomAttribute<NonNegativeValueAttribute>() is not null)
			// min = 0;

			Verbose("{Property}: Min = {Min}, Max = {Max}", prop, min, max);

			stepper = new NumericStepper { ID = $"{Prop.Name} stepper", Increment = 1, MaximumDecimalPlaces = 0, MinValue = min, MaxValue = max, ToolTip = $"Valid range is [{min}...{max}]", Style = Appearance.Styles.GeneralTextual };
			stepper.ValueChanged += (sender, _) =>
			{
				int value = (int)((NumericStepper)sender!).Value;
				Verbose("Property {Property} changed to {Value}", Prop, value);
				Prop.SetValue(Target.RenderOptions, value);
			};
			TableCell.Control = stepper;
		}

		/// <inheritdoc/>
		internal override void UpdateDisplayedFromTarget()
		{
			stepper.Value = (int)Prop.GetValue(Target.RenderOptions)!;
		}

		public override void Dispose()
		{
			stepper.Dispose();
		}
	}

	private sealed class EnumEditor<T> : PropertyEditorView where T : struct, Enum
	{
		private readonly EnumDropDown<T> dropDown;

		/// <inheritdoc/>
		public EnumEditor(RenderOptionSelectorPanel target, PropertyInfo prop, TableCell tableCell) : base(target, prop, tableCell)
		{
			Verbose("{Property}: Possible enum values are {Values}", prop, Enum.GetValues<T>());

			dropDown = new EnumDropDown<T> { ID = $"{Prop.Name} dropdown", Style = Appearance.Styles.GeneralTextual };
			dropDown.SelectedValueChanged += (sender, _) =>
			{
				T value = ((EnumDropDown<T>)sender!).SelectedValue;
				Verbose("Property {Property} changed to {Value}", Prop, value);
				Prop.SetValue(Target.RenderOptions, value);
			};
			TableCell.Control = dropDown;
		}

		/// <inheritdoc/>
		internal override void UpdateDisplayedFromTarget()
		{
			dropDown.SelectedValue = (T)Prop.GetValue(Target.RenderOptions)!;
		}

		/// <inheritdoc/>
		public override void Dispose()
		{
			dropDown.Dispose();
		}
	}

	private sealed class FloatEditor : PropertyEditorView
	{
		private readonly NumericStepper stepper;

		/// <inheritdoc/>
		public FloatEditor(RenderOptionSelectorPanel target, PropertyInfo prop, TableCell tableCell) : base(target, prop, tableCell)
		{
			//TODO: Infinities don't display properly for some reason
			const double min = double.NegativeInfinity;
			const double max = double.PositiveInfinity;
			// //It's a record so must have a backing field, don't have to worry about null refs
			// FieldInfo backingField = prop.DeclaringType!.GetField($"<{Prop.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
			// if ((Prop.GetCustomAttribute<NonNegativeValueAttribute>() ?? backingField.GetCustomAttribute<NonNegativeValueAttribute>()) is not null)
			//Honestly why the hell do I have to do this microsoft????
			//Just apply it to the field, property and parameter, please for the love of god
			// if (typeof(RenderOptions).GetConstructors()[0].GetParameters().FirstOrDefault(p => p.Name == prop.Name)?.GetCustomAttribute<NonNegativeValueAttribute>() is not null)
			// min = 0f;

			Verbose("{Property}: Min = {Min}, Max = {Max}", prop, min, max);

			stepper = new NumericStepper { ID = $"{Prop.Name} stepper", Increment = 1, MaximumDecimalPlaces = 5, DecimalPlaces = 1, MinValue = min, MaxValue = max, ToolTip = $"Valid range is [{min}...{max}]", Style = Appearance.Styles.GeneralTextual };
			stepper.ValueChanged += (sender, _) =>
			{
				float value = (float)((NumericStepper)sender!).Value;
				Verbose("Property {Property} changed to {Value}", Prop, value);
				Prop.SetValue(Target.RenderOptions, value);
			};
			TableCell.Control = stepper;
		}

		/// <inheritdoc/>
		internal override void UpdateDisplayedFromTarget()
		{
			stepper.Value = (float)Prop.GetValue(Target.RenderOptions)!;
		}

		/// <inheritdoc/>
		public override void Dispose()
		{
			stepper.Dispose();
		}
	}

#endregion


#region Boilerplate

	// ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
	private readonly DropDown sceneSelectDropdown;

	/// <summary>List of all the property editors for the <see cref="RenderOptions"/></summary>
	private readonly List<PropertyEditorView> renderOptionsPropertyEditors = new();

	/// <summary>Base class for an editor view for editing a property of a <see cref="RenderOptions"/> instance</summary>
	private abstract class PropertyEditorView : IDisposable
	{
		//We use a panel instead of RenderOptions as a target since the options being selected can change
		protected PropertyEditorView(RenderOptionSelectorPanel target, PropertyInfo prop, TableCell tableCell)
		{
			Target    = target;
			Prop      = prop;
			TableCell = tableCell;
		}

		protected          RenderOptionSelectorPanel Target    { get; }
		protected internal PropertyInfo              Prop      { get; }
		protected          TableCell                 TableCell { get; }

		/// <inheritdoc/>
		public abstract void Dispose();

		internal abstract void UpdateDisplayedFromTarget();
	}

	private void UpdateRenderOptionEditorsFromVariable()
	{
		Debug("Updating editor views from variables");
		for (int i = 0; i < renderOptionsPropertyEditors.Count; i++)
		{
			PropertyEditorView propertyEditorView = renderOptionsPropertyEditors[i];
			Verbose("Updating property view for property {Property}", propertyEditorView.Prop);
			propertyEditorView.UpdateDisplayedFromTarget();
		}
	}

	/// <summary>The render options displayed by this instance</summary>
	public RenderOptions RenderOptions { get; }

	/// <summary>The scene displayed by this instance</summary>
	public Scene Scene { get; private set; }

	/// <inheritdoc/>
	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
		renderOptionsPropertyEditors.ForEach(v => v.Dispose());
	}

#endregion
}