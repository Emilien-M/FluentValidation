﻿#region License
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
	using Microsoft.AspNetCore.Http;
	using Microsoft.AspNetCore.Mvc;
	using Microsoft.AspNetCore.Mvc.Controllers;
	using Microsoft.AspNetCore.Mvc.Internal;
	using Microsoft.AspNetCore.Mvc.ModelBinding;
	using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

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
}