﻿// <copyright file="ObservableWeaver.cs" company="Jan-Willem Spuij">
// Copyright 2019 Jan-Willem Spuij
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy,
// modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom
// the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
// ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// </copyright>

namespace Cortex.Net.Fody
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text;
    using Cortex.Net.Api;
    using Cortex.Net.Fody.Properties;
    using Cortex.Net.Types;
    using global::Fody;
    using Mono.Cecil;
    using Mono.Cecil.Cil;

    /// <summary>
    /// Weaves properties and classes decorated with an <see cref="ObservableAttribute"/>.
    /// </summary>
    internal class ObservableWeaver : ObservableObjectWeaverBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ObservableWeaver"/> class.
        /// </summary>
        /// <param name="parentWeaver">A reference to the Parent Cortex.Net weaver.</param>
        /// <param name="processorQueue">The queue to add ILProcessor actions to.</param>
        /// <exception cref="ArgumentNullException">When any of the arguments is null.</exception>
        public ObservableWeaver(BaseModuleWeaver parentWeaver, ISharedStateAssignmentILProcessorQueue processorQueue)
            : base(parentWeaver, processorQueue)
        {
        }

        /// <summary>
        /// Executes this observable weaver.
        /// </summary>
        internal void Execute()
        {
            var decoratedProperties = from t in this.ParentWeaver.ModuleDefinition.GetTypes()
                                   from m in t.Properties
                                   where
                                      t != null &&
                                      t.IsClass &&
                                      t.BaseType != null &&
                                      m != null &&
                                      m.CustomAttributes != null &&
                                      m.CustomAttributes.Any(x => x.AttributeType.FullName == typeof(ObservableAttribute).FullName)
                                   select m;

            foreach (var decoratedProperty in decoratedProperties.ToList())
            {
                this.WeaveProperty(decoratedProperty, typeof(DeepEnhancer));
            }

            var decoratedClasses = from t in this.ParentWeaver.ModuleDefinition.GetTypes()
                                      where
                                         t != null &&
                                         t.IsClass &&
                                         t.BaseType != null &&
                                         t.CustomAttributes != null &&
                                         t.CustomAttributes.Any(x => x.AttributeType.FullName == typeof(ObservableAttribute).FullName)
                                      select t;

            foreach (var decoratedClass in decoratedClasses.ToList())
            {
                this.WeaveClass(decoratedClass);
            }
        }

        /// <summary>
        /// Weaves an entire class.
        /// </summary>
        /// <param name="decoratedClass">The class that was decorated with the attribute.</param>
        private void WeaveClass(TypeDefinition decoratedClass)
        {
            var observableAttribute = decoratedClass.CustomAttributes.Single(x => x.AttributeType.FullName == typeof(ObservableAttribute).FullName);

            var defaultEhancerType = typeof(DeepEnhancer);
            var enhancerType = defaultEhancerType;
            foreach (var constructorArgument in observableAttribute.ConstructorArguments)
            {
                if (constructorArgument.Type.FullName == typeof(Type).FullName)
                {
                    enhancerType = constructorArgument.Value as Type;
                }
            }

            foreach (var property in decoratedClass.Properties.Where(x => x.GetMethod != null && x.GetMethod.IsPublic && x.SetMethod != null && x.SetMethod.IsPublic))
            {
                if (property.GetMethod.CustomAttributes.Any(x => x.AttributeType.FullName == typeof(CompilerGeneratedAttribute).FullName))
                {
                    this.WeaveProperty(property, enhancerType);
                }
            }
        }

        /// <summary>
        /// Weaves a property on an observable object.
        /// </summary>
        /// <param name="property">The property to make observable.</param>
        /// <param name="defaultEnhancer">The type of the default Enhancer.</param>
        private void WeaveProperty(PropertyDefinition property, Type defaultEnhancer)
        {
            var module = property.Module;
            var getMethod = property.GetMethod;
            var setMethod = property.SetMethod;
            var declaringType = property.DeclaringType;

            if (!getMethod.CustomAttributes.Any(x => x.AttributeType.FullName == typeof(CompilerGeneratedAttribute).FullName))
            {
                this.ParentWeaver.LogWarning(string.Format(CultureInfo.CurrentCulture, Resources.PropertyNotAutogenerated, property.Name, declaringType.Name));
                return;
            }

            if (setMethod == null)
            {
                this.ParentWeaver.LogWarning(string.Format(CultureInfo.CurrentCulture, Resources.NoSetterForObservable, property.Name, declaringType.Name));
                return;
            }

            // property name
            var propertyName = property.Name;
            var observableAttribute = property.CustomAttributes.SingleOrDefault(x => x.AttributeType.FullName == typeof(ObservableAttribute).FullName);

            // default enhancer
            var defaultEhancerType = module.ImportReference(defaultEnhancer);
            var enhancerType = defaultEhancerType;

            if (observableAttribute != null)
            {
                foreach (var constructorArgument in observableAttribute.ConstructorArguments)
                {
                    if (constructorArgument.Type.FullName == typeof(string).FullName)
                    {
                        propertyName = constructorArgument.Value as string;
                    }

                    if (constructorArgument.Type.FullName == typeof(Type).FullName)
                    {
                        enhancerType = module.ImportReference(constructorArgument.Value as Type);
                    }
                }
            }

            // Get the backing field and remove it.
            var backingField = property.GetBackingField();
            module.ImportReference(backingField);
            declaringType.Fields.Remove(backingField);

            // get or create the ObservableObjectField.
            FieldDefinition observableObjectField = declaringType.Fields.FirstOrDefault(x => x.FieldType.FullName == typeof(ObservableObject).FullName);
            if (observableObjectField is null)
            {
                var observableFieldTypeReference = module.ImportReference(typeof(ObservableObject));
                observableObjectField = declaringType.CreateField(observableFieldTypeReference, InnerObservableObjectFieldName);

                // push IL code for initialization of observableObject to the queue to emit in the ISharedState setter.
                this.ProcessorQueue.SharedStateAssignmentQueue.Enqueue(
                    (declaringType,
                    (processor, sharedStateBackingField) => this.EmitObservableObjectInit(
                        processor,
                        declaringType.Name,
                        defaultEhancerType,
                        sharedStateBackingField,
                        observableObjectField)));
            }

            // push IL code for initialization of a property to the queue to emit in the ISharedState setter.
            this.ProcessorQueue.SharedStateAssignmentQueue.Enqueue(
                (declaringType,
                (processor, sharedStateBackingField) => this.EmitObservablePropertyAdd(
                    processor,
                    propertyName,
                    property.PropertyType,
                    enhancerType,
                    sharedStateBackingField,
                    observableObjectField)));

            this.RewriteGetMethod(getMethod, observableObjectField, propertyName, property.PropertyType);
            this.RewriteSetMethod(setMethod, observableObjectField, propertyName, property.PropertyType);
        }

        /// <summary>
        /// Rewrites the Get Method of the Property.
        /// </summary>
        /// <param name="getMethod">The get method of the property.</param>
        /// <param name="observableObjectField">The field for the observable object.</param>
        /// <param name="propertyName">The name of the property.</param>
        /// <param name="propertyType">The property Type.</param>
        private void RewriteGetMethod(MethodDefinition getMethod, FieldDefinition observableObjectField, string propertyName, TypeReference propertyType)
        {
            getMethod.Body.Instructions.Clear();
            var processor = getMethod.Body.GetILProcessor();

            var observableObjectType = this.ParentWeaver.ModuleDefinition.ImportReference(typeof(ObservableObject)).Resolve();
            var observableObjectReadPropertyMethod = new GenericInstanceMethod(observableObjectType.Methods.FirstOrDefault(m => m.Name == "Read"));
            observableObjectReadPropertyMethod.GenericArguments.Add(propertyType);

            processor.Emit(OpCodes.Ldarg_0);
            processor.Emit(OpCodes.Ldfld, observableObjectField);
            processor.Emit(OpCodes.Ldstr, propertyName);
            processor.Emit(OpCodes.Callvirt, this.ParentWeaver.ModuleDefinition.ImportReference(observableObjectReadPropertyMethod));
            processor.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// Rewrites the Set Method of the Property.
        /// </summary>
        /// <param name="setMethod">The set method of the property.</param>
        /// <param name="observableObjectField">The field for the observable object.</param>
        /// <param name="propertyName">The name of the property.</param>
        /// <param name="propertyType">The property Type.</param>
        private void RewriteSetMethod(MethodDefinition setMethod, FieldDefinition observableObjectField, string propertyName, TypeReference propertyType)
        {
            setMethod.Body.Instructions.Clear();
            var processor = setMethod.Body.GetILProcessor();

            var observableObjectType = this.ParentWeaver.ModuleDefinition.ImportReference(typeof(ObservableObject)).Resolve();
            var observableObjectWritePropertyMethod = new GenericInstanceMethod(observableObjectType.Methods.FirstOrDefault(m => m.Name == "Write"));
            observableObjectWritePropertyMethod.GenericArguments.Add(propertyType);

            processor.Emit(OpCodes.Ldarg_0);
            processor.Emit(OpCodes.Ldfld, observableObjectField);
            processor.Emit(OpCodes.Ldstr, propertyName);
            processor.Emit(OpCodes.Ldarg_1);
            processor.Emit(OpCodes.Callvirt, this.ParentWeaver.ModuleDefinition.ImportReference(observableObjectWritePropertyMethod));
            processor.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// Emits the IL code to add an Observable property to the observable object. for the <see cref="IObservableObject.SharedState"/> setter.
        /// </summary>
        /// <param name="processor">The <see cref="ILProcessor"/> instance that will generate the setter body.</param>
        /// <param name="propertyName">The name of the object.</param>
        /// <param name="propertyType">The type reference of Property.</param>
        /// <param name="enhancerType">The type reference of the enhancer to use.</param>
        /// <param name="sharedStateBackingField">A <see cref="FieldReference"/> to the backing field of the Shared STate.</param>
        /// <param name="observableObjectField">A <see cref="FieldDefinition"/> for the field where the <see cref="ObservableObject"/> instance is kept.</param>
        private void EmitObservablePropertyAdd(
            ILProcessor processor,
            string propertyName,
            TypeReference propertyType,
            TypeReference enhancerType,
            FieldReference sharedStateBackingField,
            FieldDefinition observableObjectField)
        {
            var typeVariable = new VariableDefinition(propertyType);
            var getTypeFromHandlerMethod = this.ParentWeaver.ModuleDefinition.ImportReference(this.ParentWeaver.ModuleDefinition.ImportReference(typeof(Type)).Resolve().Methods.Single(x => x.Name == "GetTypeFromHandle"));
            var getEnhancerMethod = this.ParentWeaver.ModuleDefinition.ImportReference(this.ParentWeaver.ModuleDefinition.ImportReference(typeof(Core.ActionExtensions)).Resolve().Methods.Single(x => x.Name == "GetEnhancer"));

            var observableObjectType = this.ParentWeaver.ModuleDefinition.ImportReference(typeof(ObservableObject)).Resolve();
            var observableObjectAddPropertyMethod = new GenericInstanceMethod(observableObjectType.Methods.FirstOrDefault(m => m.Name == "AddObservableProperty"));
            observableObjectAddPropertyMethod.GenericArguments.Add(propertyType);

            int index = processor.Body.Variables.Count;
            processor.Body.Variables.Add(typeVariable);

            var instructions = new List<Instruction>
            {
                // this.observableObject.AddObservableProperty<T>(propertyName, default(propertyType), Cortex.Net.Core.ActionExtensions.GetEnhancer((ISharedState)sharedState, typeof(enhancer)));
                processor.Create(OpCodes.Ldarg_0),
                processor.Create(OpCodes.Ldfld, observableObjectField),
                processor.Create(OpCodes.Ldstr, propertyName),
                processor.Create(OpCodes.Ldloca_S, typeVariable),
                processor.Create(OpCodes.Initobj, propertyType),
                processor.Ldloc(index),
                processor.Create(OpCodes.Ldarg_0),
                processor.Create(OpCodes.Ldfld, sharedStateBackingField),
                processor.Create(OpCodes.Ldtoken, enhancerType),
                processor.Create(OpCodes.Call, getTypeFromHandlerMethod),
                processor.Create(OpCodes.Call, getEnhancerMethod),
                processor.Create(OpCodes.Callvirt, this.ParentWeaver.ModuleDefinition.ImportReference(observableObjectAddPropertyMethod)),
            };

            foreach (var instruction in instructions)
            {
                processor.Append(instruction);
            }
        }
    }
}