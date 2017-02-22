﻿namespace UnityEditor.Experimental.EditorVR.Actions
{
	[ActionMenuItem("Redo", ActionMenuItemAttribute.DefaultActionSectionName, 1)]
	internal sealed class Redo : BaseAction
	{
		public override void ExecuteAction()
		{
#if UNITY_EDITOR
			UnityEditor.Undo.PerformRedo();
#endif
		}
	}
}