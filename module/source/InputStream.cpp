/*************************************************************************
 * MultiLibrary - danielga.bitbucket.org/multilibrary
 * A C++ library that covers multiple low level systems.
 *------------------------------------------------------------------------
 * Copyright (c) 2015, Daniel Almeida
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 *
 * 1. Redistributions of source code must retain the above copyright
 * notice, this list of conditions and the following disclaimer.
 *
 * 2. Redistributions in binary form must reproduce the above copyright
 * notice, this list of conditions and the following disclaimer in the
 * documentation and/or other materials provided with the distribution.
 *
 * 3. Neither the name of the copyright holder nor the names of its
 * contributors may be used to endorse or promote products derived from
 * this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 * "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 * LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
 * A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
 * HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
 * LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
 * DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
 * THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 *
 *************************************************************************/

#include <InputStream.hpp>
#include <cassert>

namespace MultiLibrary
{

InputStream &InputStream::operator>>( bool &data )
{
	bool value;
	if( Read( &value, sizeof( bool ) ) == sizeof( bool ) )
		data = value;

	return *this;
}

InputStream &InputStream::operator>>( int8_t &data )
{
	int8_t value;
	if( Read( &value, sizeof( int8_t ) ) == sizeof( int8_t ) )
		data = value;

	return *this;
}

InputStream &InputStream::operator>>( uint8_t &data )
{
	uint8_t value;
	if( Read( &value, sizeof( uint8_t ) ) == sizeof( uint8_t ) )
		data = value;

	return *this;
}

InputStream &InputStream::operator>>( int16_t &data )
{
	int16_t value;
	if( Read( &value, sizeof( int16_t ) ) == sizeof( int16_t ) )
		data = value;

	return *this;
}

InputStream &InputStream::operator>>( uint16_t &data )
{
	uint16_t value;
	if( Read( &value, sizeof( uint16_t ) ) == sizeof( uint16_t ) )
		data = value;

	return *this;
}

InputStream &InputStream::operator>>( int32_t &data )
{
	int32_t value;
	if( Read( &value, sizeof( int32_t ) ) == sizeof( int32_t ) )
		data = value;

	return *this;
}

InputStream &InputStream::operator>>( uint32_t &data )
{
	uint32_t value;
	if( Read( &value, sizeof( uint32_t ) ) == sizeof( uint32_t ) )
		data = value;

	return *this;
}

InputStream &InputStream::operator>>( int64_t &data )
{
	int64_t value;
	if( Read( &value, sizeof( int64_t ) ) == sizeof( int64_t ) )
		data = value;

	return *this;
}

InputStream &InputStream::operator>>( uint64_t &data )
{
	uint64_t value;
	if( Read( &value, sizeof( uint64_t ) ) == sizeof( uint64_t ) )
		data = value;

	return *this;
}

InputStream &InputStream::operator>>( float &data )
{
	float value;
	if( Read( &value, sizeof( float ) ) == sizeof( float ) )
		data = value;

	return *this;
}

InputStream &InputStream::operator>>( double &data )
{
	double value;
	if( Read( &value, sizeof( double ) ) == sizeof( double ) )
		data = value;

	return *this;
}

InputStream &InputStream::operator>>( char &data )
{
	char value;
	if( Read( &value, sizeof( char ) ) == sizeof( char ) )
		data = value;

	return *this;
}

InputStream &InputStream::operator>>( char *data )
{
	assert( data != nullptr );

	char ch = '\0';
	size_t offset = 0;
	while( Read( &ch, sizeof( ch ) ) == sizeof( ch ) )
	{
		data[offset] = ch;

		if( ch == '\0' )
			break;

		++offset;
	}

	return *this;
}

InputStream &InputStream::operator>>( std::string &data )
{
	char ch = '\0';
	while( Read( &ch, sizeof( ch ) ) == sizeof( ch ) && ch != '\0' )
		data += ch;

	return *this;
}

InputStream &InputStream::operator>>( wchar_t &data )
{
	wchar_t value;
	if( Read( &value, sizeof( value ) ) == sizeof( value ) )
		data = value;

	return *this;
}

InputStream &InputStream::operator>>( wchar_t *data )
{
	assert( data != nullptr );

	wchar_t ch = L'\0';
	size_t offset = 0;
	while( Read( &ch, sizeof( ch ) ) == sizeof( ch ) )
	{
		data[offset] = ch;

		if( ch == L'\0' )
			break;

		++offset;
	}

	return *this;
}

InputStream &InputStream::operator>>( std::wstring &data )
{
	wchar_t ch = L'\0';
	while( Read( &ch, sizeof( ch ) ) == sizeof( ch ) && ch != L'\0' )
		data += ch;

	return *this;
}

} // namespace MultiLibrary