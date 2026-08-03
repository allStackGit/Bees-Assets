using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace Bees.Tests.EditMode
{
    internal static class RuntimeAssembly
    {
        private static Assembly _assembly;

        public static Type GetType(string fullName)
        {
            if (_assembly == null)
            {
                _assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .Single(assembly => assembly.GetName().Name == "Assembly-CSharp");
            }

            return _assembly.GetType(fullName, throwOnError: true);
        }

        public static object GetField(object instance, string fieldName)
        {
            return GetFieldInfo(instance, fieldName).GetValue(instance);
        }

        public static object GetStaticField(Type type, string fieldName)
        {
            return GetStaticFieldInfo(type, fieldName).GetValue(null);
        }

        public static void SetField(object instance, string fieldName, object value)
        {
            GetFieldInfo(instance, fieldName).SetValue(instance, value);
        }

        public static void SetStaticField(Type type, string fieldName, object value)
        {
            GetStaticFieldInfo(type, fieldName).SetValue(null, value);
        }

        public static object Invoke(object instance, string methodName, params object[] arguments)
        {
            MethodInfo method = instance.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (method == null)
            {
                throw new MissingMethodException(instance.GetType().FullName, methodName);
            }

            return method.Invoke(instance, arguments);
        }

        public static object InvokeStatic(Type type, string methodName, params object[] arguments)
        {
            MethodInfo method = type.GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (method == null)
            {
                throw new MissingMethodException(type.FullName, methodName);
            }

            return method.Invoke(null, arguments);
        }

        public static object CreateUninitialized(string fullName)
        {
            return FormatterServices.GetUninitializedObject(GetType(fullName));
        }

        public static int GetCount(object collection)
        {
            if (collection is ICollection nonGenericCollection)
            {
                return nonGenericCollection.Count;
            }

            PropertyInfo count = collection.GetType().GetProperty("Count");
            if (count == null)
            {
                throw new MissingMemberException(collection.GetType().FullName, "Count");
            }

            return (int)count.GetValue(collection);
        }

        public static void AddToCollection(object collection, object value)
        {
            MethodInfo add = collection.GetType().GetMethod("Add");
            if (add == null)
            {
                throw new MissingMethodException(collection.GetType().FullName, "Add");
            }

            add.Invoke(collection, new[] { value });
        }

        private static FieldInfo GetFieldInfo(object instance, string fieldName)
        {
            FieldInfo field = instance.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (field == null)
            {
                throw new MissingFieldException(instance.GetType().FullName, fieldName);
            }

            return field;
        }

        private static FieldInfo GetStaticFieldInfo(Type type, string fieldName)
        {
            FieldInfo field = type.GetField(
                fieldName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (field == null)
            {
                throw new MissingFieldException(type.FullName, fieldName);
            }

            return field;
        }
    }
}
