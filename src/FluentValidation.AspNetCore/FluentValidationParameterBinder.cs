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


namespace FluentValidation.AspNetCore {
	using System;
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

				var requiredErrorsNotHandledByFv = FluentValidationObjectModelValidator.RemoveImplicitRequiredErrors(actionContext);
				
				if (modelBindingContext.Model != null) {
					var customizations = FluentValidationObjectModelValidator.GetCustomizations(actionContext, modelBindingContext.Model.GetType(), modelBindingContext.ModelName);
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
				FluentValidationObjectModelValidator.ReApplyImplicitRequiredErrorsNotHandledByFV(requiredErrorsNotHandledByFv);

				// Remove duplicates. This can happen if someone has implicit child validation turned on and also adds an explicit child validator.
				FluentValidationObjectModelValidator.RemoveDuplicateModelstateEntries(actionContext);
			}
		}
	}
}