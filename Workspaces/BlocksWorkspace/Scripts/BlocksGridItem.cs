#if UNITY_EDITOR
using System;
using System.Collections;
using UnityEditor.Experimental.EditorVR.Core;
using UnityEditor.Experimental.EditorVR.Extensions;
using UnityEditor.Experimental.EditorVR.Handles;
using UnityEditor.Experimental.EditorVR.Helpers;
using UnityEditor.Experimental.EditorVR.Proxies;
using UnityEditor.Experimental.EditorVR.Utilities;
using UnityEngine;
using UnityEngine.InputNew;
using UnityEngine.UI;

namespace UnityEditor.Experimental.EditorVR.Workspaces
{
    class BlocksGridItem : DraggableListItem<BlocksAsset, string>, IPlaceSceneObject, IUsesSpatialHash,
        IUsesViewerBody, IRayVisibilitySettings, IRequestFeedback, IRayToNode, IUsesGrouping
    {
        const float k_PreviewDuration = 0.1f;
        const float k_MinPreviewScale = 0.01f;
        const float k_IconPreviewScale = 0.1f;
        const float k_MaxPreviewScale = 0.2f;
        const float k_TransitionDuration = 0.1f;
        const float k_ScaleBump = 1.1f;

        const float k_InitializeDelay = 0.5f; // Delay initialization for fast scrolling

        const int k_AutoHidePreviewComplexity = 10000;
        const int k_HidePreviewComplexity = 100000;

        [SerializeField]
        Text m_Text;

        [SerializeField]
        BaseHandle m_Handle;

        [SerializeField]
        Image m_TextPanel;

        [SerializeField]
        GameObject m_Icon;

        [HideInInspector]
        [SerializeField] // Serialized so that this remains set after cloning
        Transform m_PreviewObjectTransform;

        bool m_Setup;
        bool m_AutoHidePreview;
        Vector3 m_PreviewPrefabScale;
        Vector3 m_PreviewTargetScale;
        Vector3 m_PreviewPivotOffset;
        Bounds m_PreviewBounds;
        Transform m_PreviewObjectClone;
        Material m_IconMaterial;
        Vector3 m_IconScale;

        Coroutine m_PreviewCoroutine;
        Coroutine m_VisibilityCoroutine;

        float m_SetupTime = float.MaxValue;

        public float scaleFactor { private get; set; }

        public override void Setup(BlocksAsset listData)
        {
            base.Setup(listData);

            // First time setup
            if (!m_Setup)
            {
                m_IconScale = m_Icon.transform.localScale;

                m_Handle.dragStarted += OnDragStarted;
                m_Handle.dragging += OnDragging;
                m_Handle.dragEnded += OnDragEnded;

                m_Handle.hoverStarted += OnHoverStarted;
                m_Handle.hoverEnded += OnHoverEnded;

                m_IconMaterial = MaterialUtils.GetMaterialClone(m_Icon.GetComponent<Renderer>());

                m_Setup = true;
            }

            m_VisibilityCoroutine = null;
            m_Icon.transform.localScale = m_IconScale;
            m_IconMaterial.mainTexture = null;

            if (m_PreviewObjectTransform)
                ObjectUtils.Destroy(m_PreviewObjectTransform.gameObject);

            m_SetupTime = Time.time;
            UpdateVisuals();
        }

        void OnModelImportCompleted(BlocksAsset asset, GameObject prefab)
        {
            UpdateVisuals();
        }

        void OnThumbnailImportCompleted(BlocksAsset asset, Texture2D thumbnail)
        {
            UpdateVisuals();
        }

        void UpdateVisuals()
        {
            m_Text.text = data.asset.displayName;

            if (!m_PreviewObjectTransform && data.prefab)
            {
                m_Icon.SetActive(false);
                InstantiatePreview();
            }

            m_Icon.SetActive(!data.prefab);

            if (m_IconMaterial.mainTexture == null && data.thumbnail)
                m_IconMaterial.mainTexture = data.thumbnail;
        }

        public void UpdateTransforms(float scale)
        {
            if (Time.time - m_SetupTime > k_InitializeDelay)
            {
                m_SetupTime = float.MaxValue;

                // If this AssetData hasn't started fetching its asset yet, do so now
                if (!data.initialized)
                    data.Initialize();

                data.modelImportCompleted += OnModelImportCompleted;
                data.thumbnailImportCompleted += OnThumbnailImportCompleted;
            }

            scaleFactor = scale;

            // Don't scale the item while changing visibility because this would conflict with AnimateVisibility
            if (m_VisibilityCoroutine != null)
                return;

            transform.localScale = Vector3.one * scale;

            m_TextPanel.transform.localRotation = CameraUtils.LocalRotateTowardCamera(transform.parent);
        }

        void InstantiatePreview()
        {
            if (!data.prefab)
                return;

            var previewObject = Instantiate(data.prefab);
            previewObject.SetActive(true);
            m_PreviewObjectTransform = previewObject.transform;

            m_PreviewObjectTransform.position = Vector3.zero;
            m_PreviewObjectTransform.rotation = Quaternion.identity;

            m_PreviewPrefabScale = m_PreviewObjectTransform.localScale;

            // Normalize total scale to 1
            m_PreviewBounds = ObjectUtils.GetBounds(m_PreviewObjectTransform);

            // Don't show a preview if there are no renderers
            if (m_PreviewBounds.size == Vector3.zero)
            {
                ObjectUtils.Destroy(previewObject);
                return;
            }

            m_PreviewPivotOffset = m_PreviewObjectTransform.position - m_PreviewBounds.center;
            m_PreviewObjectTransform.SetParent(transform, false);

            var maxComponent = m_PreviewBounds.size.MaxComponent();
            var scaleFactor = 1 / maxComponent;
            m_PreviewTargetScale = m_PreviewPrefabScale * scaleFactor;
            m_PreviewObjectTransform.localPosition = m_PreviewPivotOffset * scaleFactor + Vector3.up * 0.5f;

            var complexity = data.complexity;
            // Auto hide previews over a smaller vert count
            if (complexity > k_AutoHidePreviewComplexity)
            {
                m_AutoHidePreview = true;
                m_PreviewObjectTransform.localScale = Vector3.zero;
            }
            else
            {
                m_PreviewObjectTransform.localScale = m_PreviewTargetScale;
                m_Icon.SetActive(false);
            }
        }

        protected override void OnDragStarted(BaseHandle handle, HandleEventData eventData)
        {
            if (data.prefab)
            {
                base.OnDragStarted(handle, eventData);

                var rayOrigin = eventData.rayOrigin;
                this.AddRayVisibilitySettings(rayOrigin, this, false, true);

                var clone = Instantiate(gameObject, transform.position, transform.rotation, transform.parent);
                var cloneItem = clone.GetComponent<BlocksGridItem>();

                if (cloneItem.m_PreviewObjectTransform)
                {
                    m_PreviewObjectClone = cloneItem.m_PreviewObjectTransform;
                    cloneItem.m_Icon.gameObject.SetActive(false);

                    m_PreviewObjectClone.gameObject.SetActive(true);
                    m_PreviewObjectClone.localScale = m_PreviewTargetScale;

                    // Destroy label
                    ObjectUtils.Destroy(cloneItem.m_TextPanel.gameObject);
                }

                m_DragObject = clone.transform;

                // Disable any SmoothMotion that may be applied to a cloned Asset Grid Item now referencing input device p/r/s
                var smoothMotion = clone.GetComponent<SmoothMotion>();
                if (smoothMotion != null)
                    smoothMotion.enabled = false;

                StartCoroutine(ShowGrabbedObject());
            }
        }

        protected override void OnDragEnded(BaseHandle handle, HandleEventData eventData)
        {
            if (data.prefab)
            {
                var gridItem = m_DragObject.GetComponent<BlocksGridItem>();

                var rayOrigin = eventData.rayOrigin;
                this.RemoveRayVisibilitySettings(rayOrigin, this);

                if (!this.IsOverShoulder(eventData.rayOrigin))
                {
                    var previewObject = gridItem.m_PreviewObjectTransform;
                    if (previewObject)
                    {
                        this.MakeGroup(previewObject.gameObject);
                        this.PlaceSceneObject(previewObject, m_PreviewPrefabScale);
                    }
                }

                StartCoroutine(HideGrabbedObject(m_DragObject.gameObject));
            }
            else
            {
                data.ImportModel();
                m_Text.text = "Importing...";
            }
        }

        void OnHoverStarted(BaseHandle handle, HandleEventData eventData)
        {
            if (m_PreviewObjectTransform && gameObject.activeInHierarchy)
            {
                if (m_AutoHidePreview)
                {
                    this.StopCoroutine(ref m_PreviewCoroutine);
                    m_PreviewCoroutine = StartCoroutine(AnimatePreview(false));
                }
                else
                {
                    m_PreviewObjectTransform.localScale = m_PreviewTargetScale * k_ScaleBump;
                }
            }

            ShowGrabFeedback(this.RequestNodeFromRayOrigin(eventData.rayOrigin));
        }

        void OnHoverEnded(BaseHandle handle, HandleEventData eventData)
        {
            if (m_PreviewObjectTransform && gameObject.activeInHierarchy)
            {
                if (m_AutoHidePreview)
                {
                    this.StopCoroutine(ref m_PreviewCoroutine);
                    m_PreviewCoroutine = StartCoroutine(AnimatePreview(true));
                }
                else
                {
                    m_PreviewObjectTransform.localScale = m_PreviewTargetScale;
                }
            }

            HideGrabFeedback();
        }

        IEnumerator AnimatePreview(bool @out)
        {
            m_Icon.SetActive(true);
            m_PreviewObjectTransform.gameObject.SetActive(true);

            var iconTransform = m_Icon.transform;
            var currentIconScale = iconTransform.localScale;
            var targetIconScale = @out ? Vector3.one : Vector3.zero;

            var currentPreviewScale = m_PreviewObjectTransform.localScale;
            var targetPreviewScale = @out ? Vector3.zero : m_PreviewTargetScale;

            var startTime = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - startTime < k_PreviewDuration)
            {
                var t = (Time.realtimeSinceStartup - startTime) / k_PreviewDuration;

                m_Icon.transform.localScale = Vector3.Lerp(currentIconScale, targetIconScale, t);
                m_PreviewObjectTransform.transform.localScale = Vector3.Lerp(currentPreviewScale, targetPreviewScale, t);
                yield return null;
            }

            m_PreviewObjectTransform.transform.localScale = targetPreviewScale;
            m_Icon.transform.localScale = targetIconScale;

            m_PreviewObjectTransform.gameObject.SetActive(!@out);
            m_Icon.SetActive(@out);

            m_PreviewCoroutine = null;
        }

        public void SetVisibility(bool visible, Action<BlocksGridItem> callback = null)
        {
            this.StopCoroutine(ref m_VisibilityCoroutine);
            m_VisibilityCoroutine = StartCoroutine(AnimateVisibility(visible, callback));
        }

        IEnumerator AnimateVisibility(bool visible, Action<BlocksGridItem> callback)
        {
            var currentTime = 0f;

            // Item should always be at a scale of zero before becoming visible
            if (visible)
            {
                transform.localScale = Vector3.zero;
            }
            else
            {
                data.modelImportCompleted -= OnModelImportCompleted;
                data.thumbnailImportCompleted -= OnThumbnailImportCompleted;
            }

            var currentScale = transform.localScale;
            var targetScale = visible ? Vector3.one * scaleFactor : Vector3.zero;

            while (currentTime < k_TransitionDuration)
            {
                currentTime += Time.deltaTime;
                transform.localScale = Vector3.Lerp(currentScale, targetScale, currentTime / k_TransitionDuration);
                yield return null;
            }

            transform.localScale = targetScale;

            if (callback != null)
                callback(this);

            m_VisibilityCoroutine = null;
        }

        // Animate the LocalScale of the asset towards a common/unified scale
        // used when the asset is magnetized/attached to the proxy, after grabbing it from the asset grid
        IEnumerator ShowGrabbedObject()
        {
            var currentLocalScale = m_DragObject.localScale;
            var currentPreviewOffset = Vector3.zero;
            var currentPreviewRotationOffset = Quaternion.identity;

            if (m_PreviewObjectClone)
                currentPreviewOffset = m_PreviewObjectClone.localPosition;

            var currentTime = 0f;
            var currentVelocity = 0f;
            const float kDuration = 1f;

            var targetScale = Vector3.one * k_IconPreviewScale;
            var pivotOffset = Vector3.zero;
            var rotationOffset = Quaternion.AngleAxis(30, Vector3.right);
            if (m_PreviewObjectClone)
            {
                var viewerScale = this.GetViewerScale();
                var maxComponent = m_PreviewBounds.size.MaxComponent() / viewerScale;
                targetScale = Vector3.one * maxComponent;

                // Object will preview at the same size when grabbed
                var previewExtents = m_PreviewBounds.extents / viewerScale;
                pivotOffset = m_PreviewPivotOffset / viewerScale;

                // If bounds are greater than offset, set to bounds
                if (previewExtents.y > pivotOffset.y)
                    pivotOffset.y = previewExtents.y;

                if (previewExtents.z > pivotOffset.z)
                    pivotOffset.z = previewExtents.z;

                if (maxComponent < k_MinPreviewScale)
                {
                    // Object will be preview at the minimum scale
                    targetScale = Vector3.one * k_MinPreviewScale;
                    pivotOffset = pivotOffset * scaleFactor + (Vector3.up + Vector3.forward) * 0.5f * k_MinPreviewScale;
                }

                if (maxComponent > k_MaxPreviewScale)
                {
                    // Object will be preview at the maximum scale
                    targetScale = Vector3.one * k_MaxPreviewScale;
                    pivotOffset = pivotOffset * scaleFactor + (Vector3.up + Vector3.forward) * 0.5f * k_MaxPreviewScale;
                }
            }

            while (currentTime < kDuration - 0.05f)
            {
                if (m_DragObject == null)
                    yield break; // Exit coroutine if m_GrabbedObject is destroyed before the loop is finished

                currentTime = MathUtilsExt.SmoothDamp(currentTime, kDuration, ref currentVelocity, 0.5f, Mathf.Infinity, Time.deltaTime);
                m_DragObject.localScale = Vector3.Lerp(currentLocalScale, targetScale, currentTime);

                if (m_PreviewObjectClone)
                {
                    m_PreviewObjectClone.localPosition = Vector3.Lerp(currentPreviewOffset, pivotOffset, currentTime);
                    m_PreviewObjectClone.localRotation = Quaternion.Lerp(currentPreviewRotationOffset, rotationOffset, currentTime); // Compensate for preview origin rotation
                }

                yield return null;
            }

            m_DragObject.localScale = targetScale;
        }

        static IEnumerator HideGrabbedObject(GameObject itemToHide)
        {
            var itemTransform = itemToHide.transform;
            var currentScale = itemTransform.localScale;
            var targetScale = Vector3.zero;
            var transitionAmount = Time.deltaTime;
            var transitionAddMultiplier = 6;
            while (transitionAmount < 1)
            {
                itemTransform.localScale = Vector3.Lerp(currentScale, targetScale, transitionAmount);
                transitionAmount += Time.deltaTime * transitionAddMultiplier;
                yield return null;
            }
            ObjectUtils.Destroy(itemToHide);
        }

        void ShowGrabFeedback(Node node)
        {
            this.AddFeedbackRequest(new ProxyFeedbackRequest
            {
                control = VRInputDevice.VRControl.Trigger1,
                node = node,
                tooltipText = "Grab"
            });
        }

        void HideGrabFeedback()
        {
            this.ClearFeedbackRequests();
        }
    }
}
#endif
