﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using dnSpy.Contracts.Controls.ToolWindows;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings.AppearanceCategory;
using dnSpy.Contracts.Text.Classification;
using dnSpy.Contracts.TreeView;
using dnSpy.Debugger.UI;
using Microsoft.VisualStudio.Text.Classification;

namespace dnSpy.Debugger.Evaluation.ViewModel.Impl {
	sealed class ValueNodesVM : ViewModelBase, IValueNodesVM {
		ITreeView IValueNodesVM.TreeView => treeView;

		sealed class RootNode : TreeNodeData {
			public override Guid Guid => Guid.Empty;
			public override object Text => null;
			public override object ToolTip => null;
			public override ImageReference Icon => ImageReference.None;
			public override void OnRefreshUI() { }
		}

		readonly ValueNodesProvider valueNodesProvider;
		readonly DebuggerSettings debuggerSettings;
		readonly DbgEvalFormatterSettings dbgEvalFormatterSettings;
		readonly ValueNodesContext valueNodesContext;
		readonly ITreeView treeView;
		readonly RootNode rootNode;
		bool isOpen;

		public ValueNodesVM(UIDispatcher uiDispatcher, ValueNodesVMOptions options, ITreeViewService treeViewService, EditValueProviderService editValueProviderService, DbgValueNodeImageReferenceService dbgValueNodeImageReferenceService, DebuggerSettings debuggerSettings, DbgEvalFormatterSettings dbgEvalFormatterSettings, IClassificationFormatMapService classificationFormatMapService, ITextElementProvider textElementProvider) {
			uiDispatcher.VerifyAccess();
			valueNodesProvider = options.NodesProvider;
			this.debuggerSettings = debuggerSettings;
			this.dbgEvalFormatterSettings = dbgEvalFormatterSettings;
			var classificationFormatMap = classificationFormatMapService.GetClassificationFormatMap(AppearanceCategoryConstants.UIMisc);
			valueNodesContext = new ValueNodesContext(uiDispatcher, options.WindowContentType, options.NameColumnName, options.ValueColumnName, options.TypeColumnName, editValueProviderService, dbgValueNodeImageReferenceService, new DbgValueNodeReaderImpl(), classificationFormatMap, textElementProvider, options.ShowYesNoMessageBox) {
				SyntaxHighlight = debuggerSettings.SyntaxHighlight,
			};

			rootNode = new RootNode();
			var tvOptions = new TreeViewOptions {
				CanDragAndDrop = false,
				IsGridView = true,
				RootNode = rootNode,
			};
			treeView = treeViewService.Create(options.TreeViewGuid, tvOptions);
		}

		// UI thread
		void ValueNodesProvider_NodesChanged(object sender, EventArgs e) {
			valueNodesContext.UIDispatcher.VerifyAccess();
			RecreateRootChildren_UI();
		}

		// UI thread
		void RecreateRootChildren_UI() {
			valueNodesContext.UIDispatcher.VerifyAccess();
			var nodes = isOpen ? valueNodesProvider.GetNodes() : Array.Empty<DbgValueNode>();
			RecreateRootChildrenCore_UI(nodes);
			VerifyChildren_UI(nodes);
		}

		// UI thread
		[Conditional("DEBUG")]
		void VerifyChildren_UI(DbgValueNode[] nodes) {
			var children = rootNode.TreeNode.Children;
			Debug.Assert(children.Count == nodes.Length);
			if (children.Count == nodes.Length) {
				for (int i = 0; i < nodes.Length; i++) {
					var node = (ValueNodeImpl)children[i].Data;
					Debug.Assert(node.DebuggerValueNode == nodes[i]);
				}
			}
		}

		// UI thread
		void RecreateRootChildrenCore_UI(DbgValueNode[] nodes) {
			valueNodesContext.UIDispatcher.VerifyAccess();
			if (nodes.Length > 0)
				nodes[0].Runtime.CloseOnContinue(nodes);

			if (nodes.Length == 0 || rootNode.TreeNode.Children.Count == 0) {
				SetNewRootChildren_UI(nodes);
				return;
			}

			// PERF: Re-use as many nodes as possible so the UI is only updated when something changes.
			// Most of the time the node's UI elements don't change (same name, value, and type).
			// Recreating these elements is slow.

			var children = rootNode.TreeNode.Children;
			int oldChildCount = children.Count;
			var toOldIndex = new Dictionary<string, List<int>>(oldChildCount, StringComparer.Ordinal);
			for (int i = 0; i < oldChildCount; i++) {
				var node = (ValueNodeImpl)children[i].Data;
				var expression = node.DebuggerValueNode.Expression;
				if (!toOldIndex.TryGetValue(expression, out var list))
					toOldIndex.Add(expression, list = new List<int>(1));
				list.Add(i);
			}

			int currentNewIndex = 0;
			int updateIndex = 0;
			for (int currentOldIndex = 0; currentNewIndex < nodes.Length;) {
				var (newIndex, oldIndex) = GetOldIndex(toOldIndex, nodes, currentNewIndex, currentOldIndex);
				Debug.Assert((oldIndex < 0) == (newIndex < 0));
				bool lastIter = oldIndex < 0;
				if (lastIter) {
					newIndex = nodes.Length;
					oldIndex = oldChildCount;

					// Check if all nodes were removed
					if (currentNewIndex == 0) {
						SetNewRootChildren_UI(nodes);
						return;
					}
				}

				int deleteCount = oldIndex - currentOldIndex;
				for (int i = deleteCount - 1; i >= 0; i--) {
					Debug.Assert(updateIndex + i < children.Count);
					children.RemoveAt(updateIndex + i);
				}

				for (; currentNewIndex < newIndex; currentNewIndex++) {
					Debug.Assert(updateIndex <= children.Count);
					children.Insert(updateIndex++, treeView.Create(new ValueNodeImpl(valueNodesContext, nodes[currentNewIndex])));
				}

				if (lastIter)
					break;
				Debug.Assert(updateIndex < children.Count);
				var reusedNode = (ValueNodeImpl)children[updateIndex++].Data;
				reusedNode.SetDebuggerValueNodeForRoot(nodes[currentNewIndex++]);
				currentOldIndex = oldIndex + 1;
			}
			while (children.Count != updateIndex)
				children.RemoveAt(children.Count - 1);
		}

		static (int newIndex, int oldIndex) GetOldIndex(Dictionary<string, List<int>> dict, DbgValueNode[] newNodes, int newIndex, int minOldIndex) {
			for (; newIndex < newNodes.Length; newIndex++) {
				if (dict.TryGetValue(newNodes[newIndex].Expression, out var list)) {
					for (int i = 0; i < list.Count; i++) {
						int oldIndex = list[i];
						if (oldIndex >= minOldIndex)
							return (newIndex, oldIndex);
					}
					return (-1, -1);
				}
			}
			return (-1, -1);
		}

		// UI thread
		void SetNewRootChildren_UI(DbgValueNode[] nodes) {
			valueNodesContext.UIDispatcher.VerifyAccess();
			rootNode.TreeNode.Children.Clear();
			foreach (var node in nodes)
				rootNode.TreeNode.AddChild(treeView.Create(new ValueNodeImpl(valueNodesContext, node)));
		}

		// UI thread
		void IValueNodesVM.Show() {
			valueNodesContext.UIDispatcher.VerifyAccess();
			InitializeDebugger_UI(enable: true);
		}

		// UI thread
		void IValueNodesVM.Hide() {
			valueNodesContext.UIDispatcher.VerifyAccess();
			InitializeDebugger_UI(enable: false);
		}

		// UI thread
		void InitializeDebugger_UI(bool enable) {
			valueNodesContext.UIDispatcher.VerifyAccess();
			isOpen = enable;
			if (enable) {
				valueNodesContext.ClassificationFormatMap.ClassificationFormatMappingChanged += ClassificationFormatMap_ClassificationFormatMappingChanged;
				debuggerSettings.PropertyChanged += DebuggerSettings_PropertyChanged;
				dbgEvalFormatterSettings.PropertyChanged += DbgEvalFormatterSettings_PropertyChanged;
				valueNodesContext.SyntaxHighlight = debuggerSettings.SyntaxHighlight;
				UpdateFormatterOptions();
				valueNodesProvider.NodesChanged += ValueNodesProvider_NodesChanged;
			}
			else {
				valueNodesContext.ClassificationFormatMap.ClassificationFormatMappingChanged -= ClassificationFormatMap_ClassificationFormatMappingChanged;
				debuggerSettings.PropertyChanged -= DebuggerSettings_PropertyChanged;
				dbgEvalFormatterSettings.PropertyChanged -= DbgEvalFormatterSettings_PropertyChanged;
				valueNodesProvider.NodesChanged -= ValueNodesProvider_NodesChanged;
			}
			RecreateRootChildren_UI();
		}

		// random thread
		void UI(Action callback) => valueNodesContext.UIDispatcher.UI(callback);

		// UI thread
		void ClassificationFormatMap_ClassificationFormatMappingChanged(object sender, EventArgs e) {
			valueNodesContext.UIDispatcher.VerifyAccess();
			RefreshThemeFields_UI();
		}

		// random thread
		void DebuggerSettings_PropertyChanged(object sender, PropertyChangedEventArgs e) =>
			UI(() => DebuggerSettings_PropertyChanged_UI(e.PropertyName));

		// UI thread
		void DebuggerSettings_PropertyChanged_UI(string propertyName) {
			valueNodesContext.UIDispatcher.VerifyAccess();
			if (propertyName == nameof(DebuggerSettings.UseHexadecimal))
				RefreshHexFields_UI();
			else if (propertyName == nameof(DebuggerSettings.SyntaxHighlight)) {
				valueNodesContext.SyntaxHighlight = debuggerSettings.SyntaxHighlight;
				RefreshThemeFields_UI();
			}
			else if (propertyName == nameof(DebuggerSettings.PropertyEvalAndFunctionCalls) || propertyName == nameof(DebuggerSettings.UseStringConversionFunction)) {
				UpdateFormatterOptions();
				const RefreshNodeOptions options =
					RefreshNodeOptions.RefreshValue |
					RefreshNodeOptions.RefreshValueControl;
				RefreshNodes(options);
			}
		}

		// random thread
		void DbgEvalFormatterSettings_PropertyChanged(object sender, PropertyChangedEventArgs e) =>
			UI(() => DbgEvalFormatterSettings_PropertyChanged_UI(e.PropertyName));

		// UI thread
		void DbgEvalFormatterSettings_PropertyChanged_UI(string propertyName) {
			valueNodesContext.UIDispatcher.VerifyAccess();
			switch (propertyName) {
			case nameof(DbgEvalFormatterSettings.ShowDeclaringTypes):
			case nameof(DbgEvalFormatterSettings.ShowNamespaces):
			case nameof(DbgEvalFormatterSettings.ShowIntrinsicTypeKeywords):
			case nameof(DbgEvalFormatterSettings.ShowTokens):
				UpdateFormatterOptions();
				const RefreshNodeOptions options =
					RefreshNodeOptions.RefreshValue |
					RefreshNodeOptions.RefreshValueControl |
					RefreshNodeOptions.RefreshType |
					RefreshNodeOptions.RefreshTypeControl;
				RefreshNodes(options);
				break;

			default:
				Debug.Fail($"Unknown property name: {propertyName}");
				break;
			}
		}

		// UI thread
		void RefreshThemeFields_UI() {
			valueNodesContext.UIDispatcher.VerifyAccess();
			const RefreshNodeOptions options =
				RefreshNodeOptions.RefreshNameControl |
				RefreshNodeOptions.RefreshValueControl |
				RefreshNodeOptions.RefreshTypeControl;
			RefreshNodes(options);
		}

		// UI thread
		void RefreshHexFields_UI() {
			valueNodesContext.UIDispatcher.VerifyAccess();
			UpdateFormatterOptions();
			const RefreshNodeOptions options =
				RefreshNodeOptions.RefreshValue |
				RefreshNodeOptions.RefreshValueControl;
			RefreshNodes(options);
		}

		// UI thread
		void RefreshNodes(RefreshNodeOptions options) {
			valueNodesContext.UIDispatcher.VerifyAccess();
			valueNodesContext.RefreshNodeOptions = options;
			treeView.RefreshAllNodes();
		}

		void UpdateFormatterOptions() {
			valueNodesContext.UIDispatcher.VerifyAccess();
			valueNodesContext.ValueNodeFormatParameters.ValueFormatterOptions = GetValueFormatterOptions(isDisplay: true);
			valueNodesContext.ValueNodeFormatParameters.TypeFormatterOptions = GetTypeFormatterOptions();
		}

		DbgValueFormatterOptions GetValueFormatterOptions(bool isDisplay) {
			var flags = DbgValueFormatterOptions.None;
			if (isDisplay)
				flags |= DbgValueFormatterOptions.Display;
			if (!debuggerSettings.UseHexadecimal)
				flags |= DbgValueFormatterOptions.Decimal;
			if (debuggerSettings.PropertyEvalAndFunctionCalls)
				flags |= DbgValueFormatterOptions.FuncEval;
			if (debuggerSettings.UseStringConversionFunction)
				flags |= DbgValueFormatterOptions.ToString;
			if (dbgEvalFormatterSettings.ShowDeclaringTypes)
				flags |= DbgValueFormatterOptions.DeclaringTypes;
			if (dbgEvalFormatterSettings.ShowNamespaces)
				flags |= DbgValueFormatterOptions.Namespaces;
			if (dbgEvalFormatterSettings.ShowIntrinsicTypeKeywords)
				flags |= DbgValueFormatterOptions.IntrinsicTypeKeywords;
			if (dbgEvalFormatterSettings.ShowTokens)
				flags |= DbgValueFormatterOptions.Tokens;
			return flags;
		}

		DbgValueFormatterTypeOptions GetTypeFormatterOptions() {
			var flags = DbgValueFormatterTypeOptions.None;
			if (dbgEvalFormatterSettings.ShowDeclaringTypes)
				flags |= DbgValueFormatterTypeOptions.DeclaringTypes;
			if (dbgEvalFormatterSettings.ShowNamespaces)
				flags |= DbgValueFormatterTypeOptions.Namespaces;
			if (dbgEvalFormatterSettings.ShowIntrinsicTypeKeywords)
				flags |= DbgValueFormatterTypeOptions.IntrinsicTypeKeywords;
			if (dbgEvalFormatterSettings.ShowTokens)
				flags |= DbgValueFormatterTypeOptions.Tokens;
			return flags;
		}

		void IDisposable.Dispose() {
			valueNodesContext.UIDispatcher.VerifyAccess();
			treeView.Dispose();
		}
	}
}