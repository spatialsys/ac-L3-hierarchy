using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using System.Collections.Generic;
using UnityEditor.Callbacks;
using UnityEditor.UIElements;
using Less3.TypeTree.Editor;
using System.Linq;

namespace Less3.Hierarchy.Editor
{
    public class L3HierarchyEditor : UnityEditor.EditorWindow
    {
        [SerializeField] private VisualTreeAsset m_VisualTreeAsset;// Assigned manually in script inspector.
        [SerializeField] private VisualTreeAsset m_element_VisualTreeAsset;// Assigned manually in script inspector.
        [SerializeField, HideInInspector] private L3Hierarchy target;
        [SerializeField] private Texture2D windowIcon;

        private TreeView treeView;
        private VisualElement rootContainer;

        private int lastEventFrame = -1;

        private Dictionary<int, L3HierarchyNodeElement> nodeElements = new Dictionary<int, L3HierarchyNodeElement>();

        [OnOpenAsset(1)]
        public static bool DoubleClickAsset(int instanceID, int line)
        {
            Object obj = EditorUtility.EntityIdToObject(instanceID);
            if (obj is L3Hierarchy asset)
            {
                OpenForAsset(asset);
                return true; // we handled the open
            }
            return false; // we did not handle the open
        }

        //on unity undo was called
        private void OnUndoRedoPerformed()
        {
            if (target != null)
            {
                RefreshTreeView();
            }
        }

        private void OnEnable()
        {
            if (target != null)
            {
                InitGUI(target);
            }
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
            EditorApplication.update += Update;//
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            EditorApplication.update -= Update;//
        }

        private void Update()
        {
            if (treeView != null && target != null)
            {
                // update selected indexes
                foreach (int i in treeView.selectedIndices)
                {
                    if (nodeElements.ContainsKey(i))
                    {
                        nodeElements[i].UpdateContent();
                    }
                }
            }
        }

        public static void OpenForAsset(L3Hierarchy asset)
        {
            var window = GetWindow<L3HierarchyEditor>();
            window.InitGUI(asset);
        }

        public void InitGUI(L3Hierarchy Hierarchy)
        {
            this.titleContent = new GUIContent(Hierarchy.name, windowIcon);
            target = Hierarchy;
            var root = m_VisualTreeAsset.CloneTree();
            rootContainer = root.Q<VisualElement>("RootContainer");
            rootVisualElement.Clear();
            rootVisualElement.Add(root);
            root.Q<Label>("TypeName").text = Hierarchy.GetType().Name;
            root.Q<Label>("ObjName").text = Hierarchy.name;

            // heirarchy type open
            root.Q<Label>("TypeName").AddManipulator(new Clickable(() =>
            {
                //open the object type script in script editor
                //get the m_script
                var so = new SerializedObject(target);
                var m_script = so.FindProperty("m_Script").objectReferenceValue;
                AssetDatabase.OpenAsset(m_script);
            }));

            treeView = root.Q<TreeView>("TreeView");
            treeView.makeItem = () => m_element_VisualTreeAsset.CloneTree();
            treeView.bindItem = (element, i) =>
            {
                var item = treeView.GetItemDataForIndex<IHierarchyNodeElement>(i);

                nodeElements[i] = new L3HierarchyNodeElement(element, item);

                //* add a right click context menu to the element
                element.AddManipulator(new ContextualMenuManipulator((ContextualMenuPopulateEvent evt) =>
                {
                    evt.menu.ClearItems();// children separators somehow (sometimes) pile up here unless we clear???

                    bool anyNonObjectsSelected = treeView.selectedIndices.Any(i => treeView.GetItemDataForIndex<IHierarchyNodeElement>(i) is not L3HierarchyNode);

                    if (treeView.selectedIndices.Count() > 1)
                    {
                        evt.menu.AppendAction($"{treeView.selectedIndices.Count()} Nodes Selected", (e) => { }, DropdownMenuAction.Status.Disabled);
                        return;
                    }
                    if (!anyNonObjectsSelected)
                    {
                        evt.menu.AppendAction("Add Child", (a) =>
                        {
                            L3TypeTreeWindow.OpenForType(item.Hierarchy.GetType(), element.worldTransform.GetPosition(), (type) =>
                            {
                                var newNode = item.Hierarchy.CreateNode(type, item as L3HierarchyNode);
                                RefreshTreeView();
                                ForceSelectNode(newNode);
                            });
                        });
                        evt.menu.AppendSeparator();
                        evt.menu.AppendAction("Duplicate Node", (a) =>
                        {
                            var newNode = item.Hierarchy.DuplicateNodeAction(item);
                            RefreshTreeView();
                            ForceSelectNode(newNode);
                        });
                        evt.menu.AppendAction("Delete Node", (a) =>
                        {
                            // warning dialogue
                            if (EditorUtility.DisplayDialog("Delete Node?", "This will delete the node and all its children. Are you sure?", "Delete", "Cancel"))
                            {
                                item.Hierarchy.DeleteNodeAction(item);
                                RefreshTreeView();
                            }
                        });
                    }
                    evt.StopPropagation();
                }));
            };
            treeView.unbindItem = (element, i) =>
            {
                if (nodeElements.ContainsKey(i))
                {
                    nodeElements.Remove(i);
                }
            };

            treeView.itemIndexChanged += (idFrom, idTo) =>
            {
                if (lastEventFrame == Time.frameCount)
                    return;

                // global index
                int destinationIndex = treeView.viewController.GetIndexForId(idFrom);
                int parentIndex = treeView.viewController.GetIndexForId(idTo);
                // index relative to new parent
                int siblingIndex = destinationIndex - parentIndex - 1;

                var nodeMoved = treeView.GetItemDataForId<IHierarchyNodeElement>(idFrom);
                var newParent = treeView.GetItemDataForId<IHierarchyNodeElement>(idTo);

                List<L3HierarchyNode> nodesToMove = new List<L3HierarchyNode>();
                List<int> orderedIndexes = treeView.selectedIndices.OrderByDescending(i => i).ToList();
                foreach (int index in orderedIndexes)
                {
                    var child = treeView.GetItemDataForIndex<IHierarchyNodeElement>(index);
                    if (child is L3HierarchyNode node)// injected nodes can not be moved.
                    {
                        nodesToMove.Add(node);
                    }
                }

                int i = 0;
                //* Set nodes as root nodes + reorder roots
                if (newParent == null)
                {
                    int d = destinationIndex - 1;
                    int precedingRootIndex = -1;
                    while (d >= 0)
                    {
                        var element = treeView.GetItemDataForIndex<IHierarchyNodeElement>(Mathf.Max(0, d));
                        if (element is L3HierarchyNode n && n.parent == null && nodesToMove.Contains(n) == false)
                        {
                            precedingRootIndex = d;
                            break;
                        }
                        d--;
                    }

                    L3HierarchyNode precedingRootNode = null;
                    if (precedingRootIndex != -1)
                    {
                        precedingRootNode = treeView.GetItemDataForIndex<IHierarchyNodeElement>(precedingRootIndex) as L3HierarchyNode;
                    }

                    foreach (var n in nodesToMove)
                    {
                        if (n.Hierarchy.ValidateParentAction(n, null))
                        {
                            n.Hierarchy.ReorderRootAction(n, precedingRootNode);// releases parents & sets as root with order.
                        }
                        precedingRootNode = n;
                    }
                }
                // * Set nodes as children to some node
                else if (newParent is L3HierarchyNode parentNode)
                {
                    foreach (var n in nodesToMove)
                    {
                        if (n.Hierarchy.ValidateParentAction(n, parentNode))
                        {
                            n.SetParentAction(parentNode, siblingIndex + i);
                            i++;
                        }
                    }
                }

                lastEventFrame = Time.frameCount;
                nodeMoved.Hierarchy.UpdateTree_EDITOR();
                RefreshTreeView();// todo remove.
            };

            treeView.selectionChanged += (items) =>
            {
                List<L3HierarchyNode> nodes = new List<L3HierarchyNode>();
                foreach (var obj in items)
                {
                    if (obj is L3HierarchyNode node)
                    {
                        nodes.Add(node);
                    }
                }
                UpdateSelction(nodes);
            };

            // * background right click.
            /*
            treeView.AddManipulator(new ContextualMenuManipulator((ContextualMenuPopulateEvent evt) =>
            {
                evt.menu.AppendAction("Add Node", (a) =>
                {
                    L3TypeTreeWindow.OpenForType(target.GetType(), evt.mousePosition, (type) =>
                    {
                        var newNode = target.CreateNode(type, null);
                        RefreshTreeView();
                    });
                });
            }));
            */

            var addButton = root.Q<ToolbarButton>("AddNodeButton");
            addButton.clicked += () =>
            {
                L3TypeTreeWindow.OpenForType(target.GetType(), addButton.worldTransform.GetPosition(), (type) =>
                {
                    var newNode = target.CreateNode(type, null);
                    RefreshTreeView();
                    ForceSelectNode(newNode);
                });
            };

            treeView.SetRootItems(BuildTreeView());
            treeView.autoExpand = true;
            treeView.Rebuild();
            treeView.ExpandAll();
            UpdateSelction(new List<L3HierarchyNode>());
        }

        private void RefreshTreeView()
        {
            if (treeView != null)
            {
                treeView.SetRootItems(BuildTreeView());
                treeView.Rebuild();
                treeView.ExpandAll();
            }
        }

        private void UpdateSelction(List<L3HierarchyNode> node)
        {
            if (node.Count > 0)
            {
                Selection.objects = node.ToArray();
            }
        }

        private void ForceSelectNode(IHierarchyNodeElement nodeElement)
        {
            if (nodeElement == null)
                return;
            List<int> indices = new List<int>();
            foreach (var kvp in nodeElements)
            {
                if (kvp.Value.node.GetHashCode() == nodeElement.GetHashCode())
                {
                    indices.Add(kvp.Key);
                    break;
                }
            }
            treeView.SetSelection(indices);
        }

        private List<TreeViewItemData<IHierarchyNodeElement>> BuildTreeView()
        {
            List<TreeViewItemData<IHierarchyNodeElement>> tree = new List<TreeViewItemData<IHierarchyNodeElement>>();

            foreach (var nodeElement in ((L3Hierarchy)target).nodes)
            {
                if (nodeElement.ParentElement == null)
                {
                    tree.Add(BuildTreeViewItem(nodeElement));
                }
            }
            return tree;
        }

        private TreeViewItemData<IHierarchyNodeElement> BuildTreeViewItem(IHierarchyNodeElement nodeElement)
        {
            var children = new List<TreeViewItemData<IHierarchyNodeElement>>();
            foreach (var child in nodeElement.ChildrenElements)
            {
                children.Add(BuildTreeViewItem(child));
            }
            return new TreeViewItemData<IHierarchyNodeElement>(nodeElement.GetHashCode(), nodeElement, children);
        }

        public static void MakeNotDraggable(VisualElement rowRoot)
        {
            // Prevent the row from starting drag interactions.
            rowRoot.RegisterCallback<PointerDownEvent>(e =>
            {
                // If your codebase starts drags on PointerDown, stopping here prevents it.
                e.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);

            rowRoot.RegisterCallback<PointerMoveEvent>(e =>
            {
                // If drags are started on move threshold, block it too.
                if ((e.pressedButtons & 1) != 0) // left button
                    e.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
        }
    }
}