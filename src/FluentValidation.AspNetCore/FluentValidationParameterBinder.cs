#region License
// Copyright (c) Jeremy Skinner (http://www.jeremyskinner.co.uk)
// 
// Licensed under the Apache License, Version 2.0 (the "License"); 
// you may not use this file except in compliance with the License. 
// You may obtain a copy of the License at 
// 
// http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software 
// distributed under the License is distributed on an "AS IS" BASIS, 
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. 
// See the License for the specific language governing permissions and 
// limitations under the License.
// 
// The latest version of this file can be found at https://github.com/jeremyskinner/FluentValidation
#endregion


using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace FluentValidation.AspNetCore {
	using System;
	using System.Collections.Generic;
	using System.ComponentModel.DataAnnotations;
	using System.Linq;
	using System.Reflection;
	using System.Threading.Tasks;
	using Microsoft.AspNetCore.Http;
	using Microsoft.AspNetCore.Mvc;
	using Microsoft.AspNetCore.Mvc.Abstractions;
	using Microsoft.AspNetCore.Mvc.Controllers;
	using Microsoft.AspNetCore.Mvc.Internal;
	using Microsoft.AspNetCore.Mvc.ModelBinding;
	using Microsoft.AspNetCore.Mvc.ModelBinding.Internal;
	using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
	using Microsoft.Extensions.Logging;

	internal class FluentValidationParameterBinder : ParameterBinder {
		private readonly IModelMetadataProvider _modelMetadataProvider;
		private readonly IModelValidatorProvider _compositeProvider;
		private readonly bool _runMvcValidation;
		private readonly bool _implicitValidationEnabled;
		private readonly ValidatorCache _validatorCache;
		private IModelValidatorProvider _fvProvider;

		public FluentValidationParameterBinder(IModelMetadataProvider modelMetadataProvider, IModelBinderFactory modelBinderFactory, CompositeModelValidatorProvider modelValidatorProvider, ILoggerFactory loggerFactory, bool runMvcValidation, bool implicitValidationEnabled) 
			: base(modelMetadataProvider, modelBinderFactory, modelValidatorProvider, loggerFactory) {
			_modelMetadataProvider = modelMetadataProvider;
			_compositeProvider = modelValidatorProvider;
			_runMvcValidation = runMvcValidation;
			_implicitValidationEnabled = implicitValidationEnabled;
			_validatorCache = new ValidatorCache();
			_fvProvider = modelValidatorProvider.ValidatorProviders.SingleOrDefault(x => x is FluentValidationModelValidatorProvider) as FluentValidationModelValidatorProvider;

		}

		public override async Task<ModelBindingResult> BindModelAsync(
			ActionContext actionContext,
			IModelBinder modelBinder,
			IValueProvider valueProvider,
			ParameterDescriptor parameter,
			ModelMetadata metadata,
			object value) {
			if (actionContext == null) {
				throw new ArgumentNullException(nameof(actionContext));
			}
			
			if (modelBinder == null) {
				throw new ArgumentNullException(nameof(modelBinder));
			}

			if (valueProvider == null) {
				throw new ArgumentNullException(nameof(valueProvider));
			}

			if (parameter == null) {
				throw new ArgumentNullException(nameof(parameter));
			}
			
			if (metadata == null) {
				throw new ArgumentNullException(nameof(metadata));
			}

			if (parameter.BindingInfo?.RequestPredicate?.Invoke(actionContext) == false) {
				return ModelBindingResult.Failed();
			}

			var modelBindingContext = DefaultModelBindingContext.CreateBindingContext(
				actionContext,
				valueProvider,
				metadata,
				parameter.BindingInfo,
				parameter.Name);
			modelBindingContext.Model = value;

			//Logger.AttemptingToBindParameterOrProperty(parameter, modelBindingContext);

			var parameterModelName = parameter.BindingInfo?.BinderModelName ?? metadata.BinderModelName;
			if (parameterModelName != null) {
				// The name was set explicitly, always use that as the prefix.
				modelBindingContext.ModelName = parameterModelName;
			}
			else if (modelBindingContext.ValueProvider.ContainsPrefix(parameter.Name)) {
				// We have a match for the parameter name, use that as that prefix.
				modelBindingContext.ModelName = parameter.Name;
			}
			else {
				// No match, fallback to empty string as the prefix.
				modelBindingContext.ModelName = string.Empty;
			}

			await modelBinder.BindModelAsync(modelBindingContext);

			//Logger.DoneAttemptingToBindParameterOrProperty(parameter, modelBindingContext);

			var modelBindingResult = modelBindingContext.Result;
//
//			if (_validatorForBackCompatOnly != null) {
//				// Since we don't have access to an IModelValidatorProvider, fall back
//				// on back-compatibility logic. In this scenario, top-level validation
//				// attributes will be ignored like they were historically.
//				if (modelBindingResult.IsModelSet) {
//					_validatorForBackCompatOnly.Validate(
//						actionContext,
//						modelBindingContext.ValidationState,
//						modelBindingContext.ModelName,
//						modelBindingResult.Model);
//				}
//			}
//			else {
//				Logger.AttemptingToValidateParameterOrProperty(parameter, modelBindingContext);

				EnforceBindRequiredAndValidate(
					actionContext,
					metadata,
					modelBindingContext,
					modelBindingResult);

//				Logger.DoneAttemptingToValidateParameterOrProperty(parameter, modelBindingContext);
//			}

			return modelBindingResult;
		}

		private void EnforceBindRequiredAndValidate(ActionContext actionContext, ModelMetadata metadata, ModelBindingContext modelBindingContext, ModelBindingResult modelBindingResult) {
			if (!modelBindingResult.IsModelSet && metadata.IsBindingRequired) {
				// Enforce BindingBehavior.Required (e.g., [BindRequired])
				var modelName = modelBindingContext.FieldName;
				var message = metadata.ModelBindingMessageProvider.MissingBindRequiredValueAccessor(modelName);
				actionContext.ModelState.TryAddModelError(modelName, message);
			}
			else if (modelBindingResult.IsModelSet || metadata.IsRequired) {
				// Enforce any other validation rules
//				var visitor = new ValidationVisitor(
//					actionContext,
//					_modelValidatorProvider,
//					_validatorCache,
//					_modelMetadataProvider,
//					modelBindingContext.ValidationState);
//
//				visitor.Validate(
//					metadata,
//					modelBindingContext.ModelName,
//					modelBindingResult.Model,
//					alwaysValidateAtTopLevel: metadata.IsRequired);

				var requiredErrorsNotHandledByFv = RemoveImplicitRequiredErrors(actionContext);
				
				if (modelBindingContext.Model != null) {
					var customizations = GetCustomizations(actionContext, modelBindingContext.Model.GetType(), modelBindingContext.ModelName);
					actionContext.HttpContext.Items["_FV_Customizations"] = Tuple.Create(modelBindingContext.Model, customizations);
				}

				// Setting as to whether we should run only FV or FV + the other validator providers
				IModelValidatorProvider validatorProvider = _runMvcValidation ? _compositeProvider : _fvProvider;

				var visitor = new FluentValidationVisitor(
					actionContext,
					validatorProvider,
					_validatorCache,
					_modelMetadataProvider,
					modelBindingContext.ValidationState) {
					ValidateChildren = _implicitValidationEnabled
				};

				visitor.Validate(metadata, modelBindingContext.ModelName, modelBindingResult.Model, alwaysValidateAtTopLevel: metadata.IsRequired);

				// Re-add errors that we took out if FV didn't add a key. 
				ReApplyImplicitRequiredErrorsNotHandledByFV(requiredErrorsNotHandledByFv);

				// Remove duplicates. This can happen if someone has implicit child validation turned on and also adds an explicit child validator.
				RemoveDuplicateModelstateEntries(actionContext);
			}
		}

		private static void RemoveDuplicateModelstateEntries(ActionContext actionContext) {
			foreach (var entry in actionContext.ModelState) {
				if (entry.Value.ValidationState == ModelValidationState.Invalid) {
					var existing = new HashSet<string>();

					foreach (var err in entry.Value.Errors.ToList()) {
						//TOList to create a copy so we can remvoe the original
						if (existing.Contains(err.ErrorMessage)) {
							entry.Value.Errors.Remove(err);
						}
						else {
							existing.Add(err.ErrorMessage);
						}
					}
				}
			}
		}

		private static void ReApplyImplicitRequiredErrorsNotHandledByFV(List<KeyValuePair<ModelStateEntry, ModelError>> requiredErrorsNotHandledByFv) {
			foreach (var pair in requiredErrorsNotHandledByFv) {
				if (pair.Key.ValidationState != ModelValidationState.Invalid) {
					pair.Key.Errors.Add(pair.Value);
					pair.Key.ValidationState = ModelValidationState.Invalid;
				}
			}
		}

		private static List<KeyValuePair<ModelStateEntry, ModelError>> RemoveImplicitRequiredErrors(ActionContext actionContext) {
			// This is all to work around the default "Required" messages.
			var requiredErrorsNotHandledByFv = new List<KeyValuePair<ModelStateEntry, ModelError>>();

			foreach (KeyValuePair<string, ModelStateEntry> entry in actionContext.ModelState) {
				List<ModelError> errorsToModify = new List<ModelError>();

				if (entry.Value.ValidationState == ModelValidationState.Invalid) {
					foreach (var err in entry.Value.Errors) {
						if (err.ErrorMessage.StartsWith(FluentValidationBindingMetadataProvider.Prefix)) {
							errorsToModify.Add(err);
						}
					}

					foreach (ModelError err in errorsToModify) {
						entry.Value.Errors.Clear();
						entry.Value.ValidationState = ModelValidationState.Unvalidated;
						requiredErrorsNotHandledByFv.Add(new KeyValuePair<ModelStateEntry, ModelError>(entry.Value, new ModelError(err.ErrorMessage.Replace(FluentValidationBindingMetadataProvider.Prefix, string.Empty))));
						;
					}
				}
			}
			return requiredErrorsNotHandledByFv;
		}

		private CustomizeValidatorAttribute GetCustomizations(ActionContext actionContext, Type type, string prefix) {

			if (actionContext?.ActionDescriptor?.Parameters == null) {
				return new CustomizeValidatorAttribute();
			}

			var descriptors = actionContext.ActionDescriptor.Parameters
				.Where(x => x.ParameterType == type)
				.Where(x => (x.BindingInfo != null && x.BindingInfo.BinderModelName != null && x.BindingInfo.BinderModelName == prefix) || x.Name == prefix || (prefix == string.Empty && x.BindingInfo?.BinderModelName == null))
				.OfType<ControllerParameterDescriptor>()
				.ToList();

			CustomizeValidatorAttribute attribute = null;

			if (descriptors.Count == 1) {
				attribute = descriptors[0].ParameterInfo.GetCustomAttributes(typeof(CustomizeValidatorAttribute), true).FirstOrDefault() as CustomizeValidatorAttribute;
			}
			if (descriptors.Count > 1) {
				// We found more than 1 matching with same prefix and name. 
			}

			return attribute ?? new CustomizeValidatorAttribute();
		}

	}

/*
	internal class FluentValidationObjectModelValidator : IObjectModelValidator {
		private readonly IModelMetadataProvider _modelMetadataProvider;
		private readonly bool _runMvcValidation;
		private readonly bool _implicitValidationEnabled;
		private readonly ValidatorCache _validatorCache;
		private readonly IModelValidatorProvider _compositeProvider;
		private readonly FluentValidationModelValidatorProvider _fvProvider;

		public FluentValidationObjectModelValidator(
			IModelMetadataProvider modelMetadataProvider,
			IList<IModelValidatorProvider> validatorProviders, bool runMvcValidation, bool implicitValidationEnabled) {

			if (modelMetadataProvider == null) {
				throw new ArgumentNullException(nameof(modelMetadataProvider));
			}

			if (validatorProviders == null) {
				throw new ArgumentNullException(nameof(validatorProviders));
			}

			_modelMetadataProvider = modelMetadataProvider;
			_runMvcValidation = runMvcValidation;
			_implicitValidationEnabled = implicitValidationEnabled;
			_validatorCache = new ValidatorCache();
			_fvProvider = validatorProviders.SingleOrDefault(x => x is FluentValidationModelValidatorProvider) as FluentValidationModelValidatorProvider;
			_compositeProvider = new CompositeModelValidatorProvider(validatorProviders); //.Except(new IModelValidatorProvider[]{ _fvProvider }).ToList());
		}

		public void Validate(ActionContext actionContext, ValidationStateDictionary validationState, string prefix, object model) {

			var requiredErrorsNotHandledByFv = RemoveImplicitRequiredErrors(actionContext);

			// Apply any customizations made with the CustomizeValidatorAttribute 
			var metadata = model == null ? null : _modelMetadataProvider.GetMetadataForType(model.GetType());

			if (model != null) {
				var customizations = GetCustomizations(actionContext, model.GetType(), prefix);
				actionContext.HttpContext.Items["_FV_Customizations"] = Tuple.Create(model, customizations);
			}

			// Setting as to whether we should run only FV or FV + the other validator providers
			var validatorProvider = _runMvcValidation ? _compositeProvider : _fvProvider;

			var visitor = new FluentValidationVisitor(
				actionContext,
				validatorProvider,
				_validatorCache,
				_modelMetadataProvider,
				validationState)
			{
				ValidateChildren = _implicitValidationEnabled
			};

			visitor.Validate(metadata, prefix, model);

			// Re-add errors that we took out if FV didn't add a key. 
			ReApplyImplicitRequiredErrorsNotHandledByFV(requiredErrorsNotHandledByFv);

			// Remove duplicates. This can happen if someone has implicit child validation turned on and also adds an explicit child validator.
			RemoveDuplicateModelstateEntries(actionContext);
		}

		private static void RemoveDuplicateModelstateEntries(ActionContext actionContext) {
			foreach (var entry in actionContext.ModelState) {
				if (entry.Value.ValidationState == ModelValidationState.Invalid) {
					var existing = new HashSet<string>();

					foreach (var err in entry.Value.Errors.ToList()) {
						//TOList to create a copy so we can remvoe the original
						if (existing.Contains(err.ErrorMessage)) {
							entry.Value.Errors.Remove(err);
						}
						else {
							existing.Add(err.ErrorMessage);
						}
					}
				}
			}
		}

		private static void ReApplyImplicitRequiredErrorsNotHandledByFV(List<KeyValuePair<ModelStateEntry, ModelError>> requiredErrorsNotHandledByFv) {
			foreach (var pair in requiredErrorsNotHandledByFv) {
				if (pair.Key.ValidationState != ModelValidationState.Invalid) {
					pair.Key.Errors.Add(pair.Value);
					pair.Key.ValidationState = ModelValidationState.Invalid;
				}
			}
		}

		private static List<KeyValuePair<ModelStateEntry, ModelError>> RemoveImplicitRequiredErrors(ActionContext actionContext) {
			// This is all to work around the default "Required" messages.
			var requiredErrorsNotHandledByFv = new List<KeyValuePair<ModelStateEntry, ModelError>>();

			foreach (KeyValuePair<string, ModelStateEntry> entry in actionContext.ModelState) {
				List<ModelError> errorsToModify = new List<ModelError>();

				if (entry.Value.ValidationState == ModelValidationState.Invalid) {
					foreach (var err in entry.Value.Errors) {
						if (err.ErrorMessage.StartsWith(FluentValidationBindingMetadataProvider.Prefix)) {
							errorsToModify.Add(err);
						}
					}

					foreach (ModelError err in errorsToModify) {
						entry.Value.Errors.Clear();
						entry.Value.ValidationState = ModelValidationState.Unvalidated;
						requiredErrorsNotHandledByFv.Add(new KeyValuePair<ModelStateEntry, ModelError>(entry.Value, new ModelError(err.ErrorMessage.Replace(FluentValidationBindingMetadataProvider.Prefix, string.Empty))));
						;
					}
				}
			}
			return requiredErrorsNotHandledByFv;
		}

		private CustomizeValidatorAttribute GetCustomizations(ActionContext actionContext, Type type, string prefix) {

			if (actionContext?.ActionDescriptor?.Parameters == null) {
				return new CustomizeValidatorAttribute();
			}

			var descriptors = actionContext.ActionDescriptor.Parameters
				.Where(x => x.ParameterType == type)
				.Where(x => (x.BindingInfo != null && x.BindingInfo.BinderModelName != null && x.BindingInfo.BinderModelName == prefix) || x.Name == prefix || (prefix == string.Empty && x.BindingInfo?.BinderModelName == null))
				.OfType<ControllerParameterDescriptor>()
				.ToList();

			CustomizeValidatorAttribute attribute = null;

			if (descriptors.Count == 1) {
				attribute = descriptors[0].ParameterInfo.GetCustomAttributes(typeof(CustomizeValidatorAttribute), true).FirstOrDefault() as CustomizeValidatorAttribute;
			}
			if (descriptors.Count > 1) {
				// We found more than 1 matching with same prefix and name. 
			}

			return attribute ?? new CustomizeValidatorAttribute();
		}

	}
*/

	internal class FluentValidationVisitor : ValidationVisitor {
		public bool ValidateChildren { get; set; }
		
		public FluentValidationVisitor(ActionContext actionContext, IModelValidatorProvider validatorProvider, ValidatorCache validatorCache, IModelMetadataProvider metadataProvider, ValidationStateDictionary validationState) : base(actionContext, validatorProvider, validatorCache, metadataProvider, validationState)
		{
			this.ValidateComplexTypesIfChildValidationFails = true;
		}

		protected override bool VisitChildren(IValidationStrategy strategy)
		{
			// If validting a collection property skip validation if validate children is off. 
			// However we can't actually skip it here as otherwise this will affect DataAnnotaitons validation too.
			// Instead store a list of objects to skip in the context, which the validator will check later. 
			if (!ValidateChildren && Metadata.ValidateChildren && Metadata.IsCollectionType && Metadata.MetadataKind == ModelMetadataKind.Property) {

				var skip = Context.HttpContext.Items.ContainsKey("_FV_SKIP") ? Context.HttpContext.Items["_FV_SKIP"] as HashSet<object> : null;

				if (skip == null) {
					skip = new HashSet<object>();
					Context.HttpContext.Items["_FV_SKIP"] = skip;
				}

				skip.Add(Model); 
			}

			return base.VisitChildren(strategy);
		}
	}

/*
	/// <summary>
	/// A visitor implementation that interprets <see cref="ValidationStateDictionary"/> to traverse
	/// a model object graph and perform validation.
	/// </summary>
	public class ValidationVisitor2 {
		/// <summary>
		/// Creates a new <see cref="ValidationVisitor"/>.
		/// </summary>
		/// <param name="actionContext">The <see cref="ActionContext"/> associated with the current request.</param>
		/// <param name="validatorProvider">The <see cref="IModelValidatorProvider"/>.</param>
		/// <param name="validatorCache">The <see cref="ValidatorCache"/> that provides a list of <see cref="IModelValidator"/>s.</param>
		/// <param name="metadataProvider">The provider used for reading metadata for the model type.</param>
		/// <param name="validationState">The <see cref="ValidationStateDictionary"/>.</param>
		public ValidationVisitor2(
			ActionContext actionContext,
			IModelValidatorProvider validatorProvider,
			ValidatorCache validatorCache,
			IModelMetadataProvider metadataProvider,
			ValidationStateDictionary validationState) {
			if (actionContext == null) {
				throw new ArgumentNullException(nameof(actionContext));
			}

			if (validatorProvider == null) {
				throw new ArgumentNullException(nameof(validatorProvider));
			}

			if (validatorCache == null) {
				throw new ArgumentNullException(nameof(validatorCache));
			}

			Context = actionContext;
			ValidatorProvider = validatorProvider;
			Cache = validatorCache;

			MetadataProvider = metadataProvider;
			ValidationState = validationState;

			ModelState = actionContext.ModelState;
			CurrentPath = new ValidationStack();
		}

		protected IModelValidatorProvider ValidatorProvider { get; }
		protected IModelMetadataProvider MetadataProvider { get; }
		protected ValidatorCache Cache { get; }
		protected ActionContext Context { get; }
		protected ModelStateDictionary ModelState { get; }
		protected ValidationStateDictionary ValidationState { get; }
		protected ValidationStack CurrentPath { get; }

		protected object Container { get; set; }
		protected string Key { get; set; }
		protected object Model { get; set; }
		protected ModelMetadata Metadata { get; set; }
		protected IValidationStrategy Strategy { get; set; }

		/// <summary>
		/// Indicates whether validation of a complex type should be performed if validation fails for any of its children. The default behavior is false. 
		/// </summary>
		public bool ValidateComplexTypesIfChildValidationFails { get; set; }
		/// <summary>
		/// Validates a object.
		/// </summary>
		/// <param name="metadata">The <see cref="ModelMetadata"/> associated with the model.</param>
		/// <param name="key">The model prefix key.</param>
		/// <param name="model">The model object.</param>
		/// <returns><c>true</c> if the object is valid, otherwise <c>false</c>.</returns>
		public bool Validate(ModelMetadata metadata, string key, object model) {
			return Validate(metadata, key, model, alwaysValidateAtTopLevel: false);
		}

		/// <summary>
		/// Validates a object.
		/// </summary>
		/// <param name="metadata">The <see cref="ModelMetadata"/> associated with the model.</param>
		/// <param name="key">The model prefix key.</param>
		/// <param name="model">The model object.</param>
		/// <param name="alwaysValidateAtTopLevel">If <c>true</c>, applies validation rules even if the top-level value is <c>null</c>.</param>
		/// <returns><c>true</c> if the object is valid, otherwise <c>false</c>.</returns>
		public virtual bool Validate(ModelMetadata metadata, string key, object model, bool alwaysValidateAtTopLevel) {
			if (model == null && key != null && !alwaysValidateAtTopLevel) {
				var entry = ModelState[key];
				if (entry != null && entry.ValidationState != ModelValidationState.Valid) {
					entry.ValidationState = ModelValidationState.Valid;
				}

				return true;
			}

			return Visit(metadata, key, model);
		}

		/// <summary>
		/// Validates a single node in a model object graph.
		/// </summary>
		/// <returns><c>true</c> if the node is valid, otherwise <c>false</c>.</returns>
		protected virtual bool ValidateNode() {
			var state = ModelState.GetValidationState(Key);

			// Rationale: we might see the same model state key used for two different objects.
			// We want to run validation unless it's already known that this key is invalid.
			if (state != ModelValidationState.Invalid) {
				var validators = Cache.GetValidators(Metadata, ValidatorProvider);

				var count = validators.Count;
				if (count > 0) {
					var context = new ModelValidationContext(
						Context,
						Metadata,
						MetadataProvider,
						Container,
						Model);

					var results = new List<ModelValidationResult>();
					for (var i = 0; i < count; i++) {
						results.AddRange(validators[i].Validate(context));
					}

					var resultsCount = results.Count;
					for (var i = 0; i < resultsCount; i++) {
						var result = results[i];
						var key = ModelNames.CreatePropertyModelName(Key, result.MemberName);

						// If this is a top-level parameter/property, the key would be empty,
						// so use the name of the top-level property
						if (string.IsNullOrEmpty(key) && Metadata.PropertyName != null) {
							key = Metadata.PropertyName;
						}

						ModelState.TryAddModelError(key, result.Message);
					}
				}
			}

			state = ModelState.GetFieldValidationState(Key);
			if (state == ModelValidationState.Invalid) {
				return false;
			}
			else {
				// If the field has an entry in ModelState, then record it as valid. Don't create
				// extra entries if they don't exist already.
				var entry = ModelState[Key];
				if (entry != null) {
					entry.ValidationState = ModelValidationState.Valid;
				}

				return true;
			}
		}

		protected virtual bool Visit(ModelMetadata metadata, string key, object model) {
			//RuntimeHelpers.EnsureSufficientExecutionStack();

			if (model != null && !CurrentPath.Push(model)) {
				// This is a cycle, bail.
				return true;
			}

			var entry = GetValidationEntry(model);
			key = entry?.Key ?? key ?? string.Empty;
			metadata = entry?.Metadata ?? metadata;
			var strategy = entry?.Strategy;

			if (ModelState.HasReachedMaxErrors) {
				SuppressValidation(key);
				return false;
			}
			else if (entry != null && entry.SuppressValidation) {
				// Use the key on the entry, because we might not have entries in model state.
				SuppressValidation(entry.Key);
				CurrentPath.Pop(model);
				return true;
			}

			using (StateManager.Recurse(this, key ?? string.Empty, metadata, model, strategy)) {
				if (Metadata.IsEnumerableType) {
					return VisitComplexType(DefaultCollectionValidationStrategy.Instance);
				}

				if (Metadata.IsComplexType) {
					return VisitComplexType(DefaultComplexObjectValidationStrategy.Instance);
				}

				return VisitSimpleType();
			}
		}

		// Covers everything VisitSimpleType does not i.e. both enumerations and complex types.
		protected virtual bool VisitComplexType(IValidationStrategy defaultStrategy) {
			var isValid = true;

			if (Model != null && Metadata.ValidateChildren) {
				var strategy = Strategy ?? defaultStrategy;
				isValid = VisitChildren(strategy);
			}
			else if (Model != null) {
				// Suppress validation for the entries matching this prefix. This will temporarily set
				// the current node to 'skipped' but we're going to visit it right away, so subsequent
				// code will set it to 'valid' or 'invalid'
				SuppressValidation(Key);
			}

			// Double-checking HasReachedMaxErrors just in case this model has no properties.
			// If validation has failed for any children, only validate the parent if ValidateComplexTypesIfChildValidationFails is true.
			if ((isValid || ValidateComplexTypesIfChildValidationFails) && !ModelState.HasReachedMaxErrors) {
				isValid &= ValidateNode();
			}

			return isValid;
		}

		protected virtual bool VisitSimpleType() {
			if (ModelState.HasReachedMaxErrors) {
				SuppressValidation(Key);
				return false;
			}

			return ValidateNode();
		}

		protected virtual bool VisitChildren(IValidationStrategy strategy) {
			var isValid = true;
			var enumerator = strategy.GetChildren(Metadata, Key, Model);
			var parentEntry = new ValidationEntry(Metadata, Key, Model);

			while (enumerator.MoveNext()) {
				var entry = enumerator.Current;
				var metadata = entry.Metadata;
				var key = entry.Key;
				if (metadata.PropertyValidationFilter?.ShouldValidateEntry(entry, parentEntry) == false) {
					SuppressValidation(key);
					continue;
				}

				isValid &= Visit(metadata, key, entry.Model);
			}

			return isValid;
		}

		protected virtual void SuppressValidation(string key) {
			if (key == null) {
				// If the key is null, that means that we shouldn't expect any entries in ModelState for
				// this value, so there's nothing to do.
				return;
			}

			var entries = ModelState.FindKeysWithPrefix(key);
			foreach (var entry in entries) {
				entry.Value.ValidationState = ModelValidationState.Skipped;
			}
		}

		protected virtual ValidationStateEntry GetValidationEntry(object model) {
			if (model == null || ValidationState == null) {
				return null;
			}

			ValidationStateEntry entry;
			ValidationState.TryGetValue(model, out entry);
			return entry;
		}

		protected struct StateManager : IDisposable {
			private readonly ValidationVisitor2 _visitor;
			private readonly object _container;
			private readonly string _key;
			private readonly ModelMetadata _metadata;
			private readonly object _model;
			private readonly object _newModel;
			private readonly IValidationStrategy _strategy;

			public static StateManager Recurse(
				ValidationVisitor2 visitor,
				string key,
				ModelMetadata metadata,
				object model,
				IValidationStrategy strategy) {
				var recursifier = new StateManager(visitor, model);

				visitor.Container = visitor.Model;
				visitor.Key = key;
				visitor.Metadata = metadata;
				visitor.Model = model;
				visitor.Strategy = strategy;

				return recursifier;
			}

			public StateManager(ValidationVisitor2 visitor, object newModel) {
				_visitor = visitor;
				_newModel = newModel;

				_container = _visitor.Container;
				_key = _visitor.Key;
				_metadata = _visitor.Metadata;
				_model = _visitor.Model;
				_strategy = _visitor.Strategy;
			}

			public void Dispose() {
				_visitor.Container = _container;
				_visitor.Key = _key;
				_visitor.Metadata = _metadata;
				_visitor.Model = _model;
				_visitor.Strategy = _strategy;

				_visitor.CurrentPath.Pop(_newModel);
			}
		}
	}
*/

}