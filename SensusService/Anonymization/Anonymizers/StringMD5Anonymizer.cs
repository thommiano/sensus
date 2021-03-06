﻿// Copyright 2014 The Rector & Visitors of the University of Virginia
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using SensusService.Exceptions;

namespace SensusService.Anonymization.Anonymizers
{
    public class StringMD5Anonymizer : Anonymizer
    {        
        public override string DisplayText
        {
            get
            {
                return "MD5 Hash";
            }
        }
       
        public override object Apply(object value, Protocol Protocol)
        {
            if (value == null)
                return null;
            
            string s = value as string;

            if (s == null)
                throw new SensusException("Attempted to apply string MD5 anonymizer to a non-string value.");

            return SensusServiceHelper.Get().GetMd5Hash(s);
        }
    }
}

