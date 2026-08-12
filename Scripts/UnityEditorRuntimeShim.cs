#if !UNITY_EDITOR
// Level.cs has a legacy, unused `using UnityEditor;`. Player builds do not reference the
// UnityEditor assembly, so provide an empty compile-time namespace outside the editor until
// that large legacy source file is decomposed further. No editor API is exposed at runtime.
namespace UnityEditor
{
}
#endif
