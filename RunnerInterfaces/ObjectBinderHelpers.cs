﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;

namespace RunnerInterfaces
{    
    public static class ObjectBinderHelpers
    {
        // Beware, we deserializing, DateTimes may arbitrarily be Local or UTC time.
        // Callers can normalize via DateTime.ToUniversalTime()
        // Can't really normalize here because DateTimes could be embedded deep in the target type.
        public static object BindFromString(string input, Type target)
        {
            if (target == typeof(string))
            {
                return input;
            }

            // Invoke:  success = Target.TryParse(input, out value)
            MethodInfo tryParseMethod = target.GetMethod("TryParse", new[] { typeof(string), target.MakeByRefType() });
            if (tryParseMethod != null)
            {
                object[] args = new object[] { input, null };
                bool success = (bool)tryParseMethod.Invoke(null, args);
                if (!success)
                {
                    string msg = string.Format("Parameter is illegal format to parse as type '{0}'", target.FullName);
                    throw new InvalidOperationException(msg);
                }
                return args[1];
            }

            // Look for a type converter. 
            // Do this before Enums to give it higher precedence. 
            var converter = GetConverter(target);
            if (converter != null)
            {
                if (converter.CanConvertFrom(typeof(string)))
                {
                    return converter.ConvertFrom(input);
                }
            }

            // Enum support 
            if (target.IsEnum)
            {
                return Enum.Parse(target, input, ignoreCase: true);
            }

            // It's possible we end up here if the string was JSON and we should have been using a JSON deserializer instead. 
            {
                string msg = string.Format("Can't bind from string to type '{0}'", target.FullName);
                throw new InvalidOperationException(msg);
            }
        }


        // BCL implementation may get wrong converters
        // It appears to use Type.GetType() to find a converter, and so has trouble looking up converters from different loader contexts.
        static TypeConverter GetConverter(Type type)
        {
            // $$$ There has got to be a better way than this to make TypeConverters work.
            foreach (TypeConverterAttribute attr in type.GetCustomAttributes(typeof(TypeConverterAttribute), false))
            {
                string assemblyQualifiedName = attr.ConverterTypeName;
                if (!string.IsNullOrWhiteSpace(assemblyQualifiedName))
                {
                    // Type.GetType() may fail due to loader context issues.
                    string assemblyName = type.Assembly.FullName;

                    if (assemblyQualifiedName.EndsWith(assemblyName))
                    {
                        int i = assemblyQualifiedName.IndexOf(',');
                        if (i > 0)
                        {
                            string typename = assemblyQualifiedName.Substring(0, i);

                            var a = type.Assembly;
                            var t2 = a.GetType(typename); // lookup type name relative to the 
                            if (t2 != null)
                            {
                                var instance = Activator.CreateInstance(t2);
                                return (TypeConverter)instance;
                            }
                        }
                    }
                }
            }

            return TypeDescriptor.GetConverter(type);
        }

        private static IDictionary<string, string> ConvertDict<TValue>(IDictionary<string, TValue> source)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            foreach (var kv in source)
            {
                d[kv.Key] = kv.Value.ToString();
            }
            return d;
        }

        static MethodInfo methodConvertDict = typeof(ObjectBinderHelpers).GetMethod("ConvertDict", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        // Dictionary is a copy (immune if source object gets mutated)        
        public static IDictionary<string, string> ConvertObjectToDict(object obj)
        {
            Type type = obj.GetType();

            // Does type implemnet IDictionary<string, TValue>?
            // If so, run through and call
            foreach(var typeInterface in type.GetInterfaces())
            {    
                if (typeInterface.IsGenericType)
                {
                    if (typeInterface.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                    {
                        var typeArgs = typeInterface.GetGenericArguments();
                        if (typeArgs[0] == typeof(string))
                        {
                            var m = methodConvertDict.MakeGenericMethod(typeArgs[1]);
                            IDictionary<string, string> result = (IDictionary<string, string>) m.Invoke(null, new object[] { obj });
                            return result;
                        }
                    }
                }
            }

            Dictionary<string, string> d = new Dictionary<string, string>();

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                object value = prop.GetValue(obj, null);
                if (value != null)
                {
                    if (UseToStringParser(prop.PropertyType))
                    {
                        d[prop.Name] = value.ToString();
                    }
                    else
                    {
                        d[prop.Name] = JsonCustom.SerializeObject(value);
                    }
                }
            }
            return d;
        }

        public static T ConvertDictToObject<T>(IDictionary<string, string> data) where T : new()
        {
            if (data == null)
            {
                return default(T);
            }

            var obj = new T();
            foreach (var kv in data)
            {
                var prop = typeof(T).GetProperty(kv.Key, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    object value;
                    string str = kv.Value;
                    Type type = prop.PropertyType;
                    if (UseToStringParser(prop.PropertyType))
                    {
                        value = BindFromString(str, type);
                    }
                    else
                    {
                        value = JsonCustom.DeserializeObject(str, type);
                    }
                    prop.SetValue(obj, value, null);
                }
            }

            return obj;
        }

        // We have 2 parsing formats:
        // - ToString / TryParse
        // - JSON 
        // Make sure serialization/Deserialization agree on the types.
        // Parses are *not* compatible, especially for same types. 
        private static bool UseToStringParser(Type t)
        {
            // JOSN requires strings to be quoted. 
            // The practical effect of adding some of these types just means that the values don't need to be quoted. 
            // That gives them higher compatibily with just regular strings. 
            return Utility.IsDefaultTableType(t) || (t == typeof(TimeSpan)) || (t == typeof(CloudBlobPath));
        }
    }
}