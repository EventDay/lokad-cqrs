﻿#region (c) 2010-2012 Lokad - CQRS Sample for Windows Azure - New BSD License 

// Copyright (c) Lokad 2010-2012, http://www.lokad.com
// This code is released as Open Source under the terms of the New BSD Licence

#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Sample
{
    public interface TypedSpecification<T> : spec
    {
        Action GetBefore();
        Delegate GetOn();
        Delegate GetWhen();
        IEnumerable<Assertion<T>> GetAssertions();
        Action GetFinally();
    }

    public interface Assertion<T>
    {
        IEnumerable<ExpectationResult> Assert(object fromWhen);
    }

    public interface spec
    {
        string GetName();
        void Document(RunResult result);
    }

    interface ISpecificationGenerator
    {
        IEnumerable<SpecificationToRun> GetSpecifications();
    }


    public class RootGenerator : ISpecificationGenerator
    {
        readonly Assembly _assembly;

        public RootGenerator(Assembly assembly)
        {
            _assembly = assembly;
        }

        public IEnumerable<SpecificationToRun> GetSpecifications()
        {
            return _assembly.GetTypes().SelectMany(TypeReader.GetSpecificationsIn);
        }
    }


    public static class TypeReader
    {
        public static IEnumerable<SpecificationToRun> GetSpecificationsIn(Type t)
        {
            foreach (var methodSpec in AllMethodSpecifications(t)) yield return methodSpec;
            foreach (var fieldSpec in AllFieldSpecifications(t)) yield return fieldSpec;
        }

        static IEnumerable<SpecificationToRun> AllMethodSpecifications(Type t)
        {
            foreach (var s in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (typeof(spec).IsAssignableFrom(s.ReturnType))
                {
                    var result = CallMethod(s);
                    if (result != null) yield return new SpecificationToRun((spec) result, s);
                }
                if (typeof(IEnumerable<spec>).IsAssignableFrom(s.ReturnType))
                {
                    var obj = (IEnumerable<spec>) CallMethod(s);
                    foreach (var item in obj)
                        yield return new SpecificationToRun(item, s);
                }
            }
        }

        static IEnumerable<SpecificationToRun> AllFieldSpecifications(Type t)
        {
            foreach (var m in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (typeof(spec).IsAssignableFrom(m.FieldType))
                {
                    yield return new SpecificationToRun((spec) m.GetValue(Activator.CreateInstance(t)), m);
                }
                if (typeof(IEnumerable<spec>).IsAssignableFrom(m.FieldType))
                {
                    var obj = (IEnumerable<spec>) m.GetValue(Activator.CreateInstance(t));
                    foreach (var item in obj)
                        yield return new SpecificationToRun(item, m);
                }
            }
        }

        static object CallMethod(MethodInfo methodInfo)
        {
            if (methodInfo.GetParameters().Length > 0) return null;
            var obj = Activator.CreateInstance(methodInfo.DeclaringType);
            var ret = methodInfo.Invoke(obj, null);
            return ret;
        }
    }

    public class SpecificationToRun
    {
        public readonly spec Specification;
        public readonly MemberInfo FoundOn;
        public readonly bool IsRunnable;
        public readonly string Reason;
        public readonly Exception Exception;

        public SpecificationToRun(spec specification, MemberInfo foundOn)
        {
            IsRunnable = true;
            Reason = "";
            Exception = null;
            Specification = specification;
            FoundOn = foundOn;
        }

        public SpecificationToRun(spec specification, string reason, Exception exception, MemberInfo foundOn)
        {
            FoundOn = foundOn;
            Specification = specification;
            Exception = exception;
            Reason = reason;
            IsRunnable = false;
        }
    }

    public class ExpectationResult
    {
        public bool Passed;
        public string Text;
        public Exception Exception;
        public Expression OriginalExpression;
    }


    public class RunResult
    {
        public bool Passed;
        public string Message;
        public Exception Thrown;
        public string SpecificationName;
        public List<ExpectationResult> Expectations = new List<ExpectationResult>();
        public MemberInfo FoundOnMemberInfo;
        public Delegate On;
        public object Result;

        public object GetOnResult()
        {
            return On.DynamicInvoke();
        }

        public string Name
        {
            get { return SpecificationName ?? FoundOnMemberInfo.Name; }
        }


        internal void MarkFailure(string message, Exception thrown)
        {
            Thrown = thrown;
            Message = message;
            Passed = false;
        }
    }


    public class SpecificationRunner
    {
        public RunResult RunSpecification(SpecificationToRun spec)
        {
            var method = typeof(SpecificationRunner).GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Instance);
            var tomake =
                spec.Specification.GetType().GetInterfaces().Single(
                    x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(TypedSpecification<>));
            var generic = method.MakeGenericMethod(tomake.GetGenericArguments()[0]);
            var result = (RunResult) generic.Invoke(this, new[] {spec.Specification});
            result.FoundOnMemberInfo = spec.FoundOn;
            return result;
        }

        RunResult Run<T>(TypedSpecification<T> spec)
        {
            var result = new RunResult {SpecificationName = spec.GetName()};
            try
            {
                var before = spec.GetBefore();
                before.InvokeIfNotNull();
            }
            catch (Exception ex)
            {
                result.MarkFailure("Before Failed", ex.InnerException);
                return result;
            }
            object sut = null;
            try
            {
                var given = spec.GetOn();
                sut = given.DynamicInvoke();
                result.On = given;
            }
            catch (Exception ex)
            {
                result.MarkFailure("On Failed", ex.InnerException);
                return result;
            }
            object whenResult = null;
            Delegate when;
            try
            {
                when = spec.GetWhen();
                if (when == null)
                    return new RunResult
                        {SpecificationName = spec.GetName(), Passed = false, Message = "No when on specification"};
                if (when.Method.GetParameters().Length == 1)
                    whenResult = when.DynamicInvoke(new[] {sut});
                else
                    whenResult = when.DynamicInvoke();
                if (when.Method.ReturnType != typeof(void))
                    result.Result = whenResult;
                else
                    result.Result = sut;
            }
            catch (Exception ex)
            {
                result.MarkFailure("When Failed", ex.InnerException);
                return result;
            }
            var fromWhen = when.Method.ReturnType == typeof(void) ? sut : whenResult;

            var allOk = true;
            foreach (var assertion in spec.GetAssertions())
            {
                foreach (var expectationResult in assertion.Assert(fromWhen))
                {
                    result.Expectations.Add(expectationResult);
                    if (!expectationResult.Passed)
                        allOk = false;
                }
            }
            try
            {
                var Finally = spec.GetFinally();
                Finally.InvokeIfNotNull();
            }
            catch (Exception ex)
            {
                allOk = false;
                result.Message = "Finally failed";
                result.Thrown = ex.InnerException;
            }
            result.Passed = allOk;
            return result;
        }
    }

    static class DelegateExtensions
    {
        public static object InvokeIfNotNull(this Delegate d)
        {
            return d != null ? d.DynamicInvoke() : null;
        }
    }

    public static class Extensions
    {
        public static string CleanupName(this string name)
        {
            var tmp = name.CleanupUnderScores();
            return tmp.CleanupCamelCasing();
        }

        public static string CleanupUnderScores(this string name)
        {
            if (name.Contains('_'))
                return name.Replace('_', ' ');
            return name;
        }

        public static string CleanupCamelCasing(this string name)
        {
            return Regex.Replace(name,
                "([A-Z])",
                " $1",
                RegexOptions.Compiled
                ).Trim();
        }
    }
}