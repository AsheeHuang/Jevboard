using System;
using System.Runtime.InteropServices;

namespace Jevboard
{
    // Minimal native UI Automation COM interop for the StructureChanged subscription. The managed wrapper
    // (System.Windows.Automation) throws ArgumentNullException inside its own event plumbing whenever any provider
    // raises StructureChanged without a runtime id, which terminates the process; the raw COM callback just sees null.
    // Only vtable order matters for the placeholder methods; they are never called.
    [ComImport, Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")]
    class CUIAutomation { }

    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomation
    {
        void CompareElements(); void CompareRuntimeIds();
        IUIAutomationElement GetRootElement();
        void ElementFromHandle(); void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache(); void ElementFromHandleBuildCache(); void ElementFromPointBuildCache(); void GetFocusedElementBuildCache();
        void CreateTreeWalker(); void get_ControlViewWalker(); void get_ContentViewWalker(); void get_RawViewWalker(); void get_RawViewCondition(); void get_ControlViewCondition(); void get_ContentViewCondition();
        IUIAutomationCacheRequest CreateCacheRequest();
        void CreateTrueCondition(); void CreateFalseCondition(); void CreatePropertyCondition(); void CreatePropertyConditionEx(); void CreateAndCondition(); void CreateAndConditionFromArray(); void CreateAndConditionFromNativeArray(); void CreateOrCondition(); void CreateOrConditionFromArray(); void CreateOrConditionFromNativeArray(); void CreateNotCondition();
        void AddAutomationEventHandler(); void RemoveAutomationEventHandler(); void AddPropertyChangedEventHandlerNativeArray(); void AddPropertyChangedEventHandler(); void RemovePropertyChangedEventHandler();
        void AddStructureChangedEventHandler(IUIAutomationElement element, int scope, IUIAutomationCacheRequest cacheRequest, IUIAutomationStructureChangedEventHandler handler);
    }

    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationElement
    {
        void SetFocus(); void GetRuntimeId(); void FindFirst(); void FindAll(); void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        object GetCurrentPropertyValue(int propertyId);
        void GetCurrentPropertyValueEx();
        object GetCachedPropertyValue(int propertyId);
    }

    [ComImport, Guid("B32A92B5-BC25-4078-9C08-D7EE95C48E03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationCacheRequest
    {
        void AddProperty(int propertyId);
    }

    [ComImport, Guid("E81D1B4E-11C5-42F8-9754-E7036C79F054"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationStructureChangedEventHandler
    {
        void HandleStructureChangedEvent(IUIAutomationElement sender, int changeType, [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] runtimeId);
    }

    static class Uia
    {
        public const int ProcessIdProperty = 30002, ClassNameProperty = 30012, NativeWindowHandleProperty = 30020;
        public const int TreeScopeSubtree = 7, ChildAdded = 0;
    }
}
